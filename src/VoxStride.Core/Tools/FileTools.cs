using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Execution;
using VoxStride.Core.Files;
using VoxStride.Core.Json;
using VoxStride.Core.Policy;

namespace VoxStride.Core.Tools;

/// <summary>Shared services for file tools. Allowed roots come only from local known-folder resolution, never from the model.</summary>
public sealed class FileToolServices(IKnownFolders folders, ManifestStore manifests, MoveExecutor executor, OrganizationPlanner planner, TempCleaner cleaner, IFileRemover remover, IDriveInfoSource drives)
{
    public IKnownFolders Folders { get; } = folders;
    public ManifestStore Manifests { get; } = manifests;
    public MoveExecutor Executor { get; } = executor;
    public OrganizationPlanner Planner { get; } = planner;
    public TempCleaner Cleaner { get; } = cleaner;
    public IFileRemover Remover { get; } = remover;
    public IDriveInfoSource Drives { get; } = drives;

    public static readonly string[] OrganizableFolders = ["desktop", "downloads", "documents"];

    public string? ResolveFolder(string name) => name switch
    {
        "desktop" => Folders.Resolve(KnownFolder.Desktop),
        "downloads" => Folders.Resolve(KnownFolder.Downloads),
        "documents" => Folders.Resolve(KnownFolder.Documents),
        _ => null,
    };

    public PathPolicy PolicyFor(string root) => new([root]);
}

public sealed class PreviewOrganizationTool(FileToolServices s) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "desktop_preview_organization",
        "Preview organizing top-level files of a known folder into category subfolders by file type. No changes are made. Folders, shortcuts and hidden/system files are always left alone. Returns a manifestId to apply.",
        Schema.Object(
            ("folder", Schema.Str("Which known folder", null, 16, FileToolServices.OrganizableFolders), true),
            ("includeCategories", Schema.Array("Only move these categories (empty = all known types)", Schema.Str("Category", null, 32, FileCategories.All), 11), false),
            ("excludeCategories", Schema.Array("Never move these categories", Schema.Str("Category", null, 32, FileCategories.All), 11), false),
            ("excludeExtensions", Schema.Array("Extensions never to move, e.g. .zip", Schema.Str("Extension with dot", @"^\.[A-Za-z0-9]{1,10}$", 11), 50), false),
            ("moveUnknownToOther", Schema.Bool("Move unrecognized file types into an Other folder (default false: leave them)"), false)),
        RiskTier.Read, false);

    public override ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        var root = s.ResolveFolder(Str(args, "folder"));
        if (root is null || !Directory.Exists(root)) return ValueTask.FromResult(ToolResult.Fail("folder_unavailable", "That folder could not be resolved."));
        var opts = new OrganizeOptions
        {
            IncludeCategories = Set(args["includeCategories"]),
            ExcludeCategories = Set(args["excludeCategories"]),
            ExcludeExtensions = Set(args["excludeExtensions"]),
            MoveUnknownToOther = Bool(args, "moveUnknownToOther", false),
        };
        var m = s.Planner.Preview(root, opts, s.PolicyFor(root));
        s.Manifests.Put(m);
        var msg = m.Items.Count == 0 ? "There's nothing to move." : $"I can move {m.Items.Count} files into {m.Items.Select(i => i.Category).Distinct().Count()} folders and leave {m.Skipped.Count} items as they are.";
        return ValueTask.FromResult(ToolResult.Ok(m.Summary(), msg, verified: true));
    }

    private static HashSet<string> Set(JsonNode? n) =>
        new(n is JsonArray a ? a.Select(x => x!.GetValue<string>()) : [], StringComparer.OrdinalIgnoreCase);
}

public sealed class ApplyOrganizationTool(FileToolServices s) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "desktop_apply_organization",
        "Apply a previously previewed organization manifest. Moves are never overwriting, are verified, journaled and undoable. Requires the user's local approval of the exact manifest.",
        Schema.Object(("manifestId", Schema.Str("manifestId returned by desktop_preview_organization", "^m-[0-9a-f]{12}$", 14), true)),
        RiskTier.Moderate, true);

    public override ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var m = s.Manifests.GetOrganization(Str(args, "manifestId"));
        if (m is null) return ValueTask.FromResult(ToolValidation.Invalid("manifest_unknown", "Preview expired or unknown. Preview again."));
        if (m.Items.Count == 0) return ValueTask.FromResult(ToolValidation.Invalid("nothing_to_do", "The preview contains no moves."));
        var lines = new List<string> { $"Folder: {m.Root}" };
        lines.AddRange(m.Items.GroupBy(i => i.Category).Select(g => $"{g.Count()} → {g.Key}"));
        if (m.Skipped.Count > 0) lines.Add($"{m.Skipped.Count} item(s) left untouched (folders, shortcuts, hidden, excluded)");
        lines.AddRange(m.Items.Take(25).Select(i => $"  {Path.GetFileName(i.SourcePath)} → {Path.GetRelativePath(m.Root, i.DestinationPath)}"));
        if (m.Items.Count > 25) lines.Add($"  …and {m.Items.Count - 25} more");
        lines.Add("Nothing is overwritten. You can undo afterwards.");
        return ValueTask.FromResult(ToolValidation.NeedsApproval(new ApprovalRequest(ctx.Auth.RunId, Definition.Name, StandingRuleOperations.ReversibleSameRootMove,
            m.Digest, $"Move {m.Items.Count} file(s) into category folders", lines, RiskTier.Moderate, Scope: m.Root)));
    }

    public override async ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var m = s.Manifests.GetOrganization(Str(args, "manifestId"));
        if (m is null) return ToolResult.Fail("manifest_unknown", "Preview expired.");
        if (ctx.Approval is null || ctx.Approval.ManifestDigest != m.Digest) return ToolResult.Fail("approval_mismatch", "Approval does not match this manifest.");
        s.Manifests.Consume(m.ManifestId);
        var progress = new VoxStride.Core.Events.SyncProgress<(int done, int total)>(p => ctx.Events.Publish(ctx.Auth.RunId, AgentState.Executing, "moving", $"Moved {p.done} of {p.total}", ctx.StepId, p.done, p.total));
        var r = await s.Executor.ApplyAsync(m, s.PolicyFor(m.Root), ctx, progress, ct);
        var state = new JsonObject
        {
            ["manifestId"] = m.ManifestId, ["moved"] = r.Moved, ["skipped"] = r.Skipped, ["failed"] = r.Failed, ["unknown"] = r.Unknown,
            ["undoAvailable"] = r.Moved > 0,
        };
        var outcome = r.Failed + r.Unknown + r.Skipped == 0 ? ToolOutcome.Success : r.Moved > 0 ? ToolOutcome.Partial : ToolOutcome.Failure;
        var folders = m.Items.Select(i => i.Category).Distinct().Count();
        var issues = r.Skipped + r.Failed + r.Unknown;
        var msg = r.Moved == 0 ? "I couldn't move any files — they changed or were in use, so I left everything as it was."
            : $"Done! I moved {r.Moved} of {m.Items.Count} files into {folders} folder{(folders == 1 ? "" : "s")}." + (issues > 0 ? $" {issues} couldn't be moved because they were in use or changed." : "") + " Just say \"undo that\" if you want them back.";
        return new ToolResult(outcome, state, null, msg, false, false, r.MovedDestinations, m.ManifestId, TimeSpan.Zero);
    }

    public override async ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct)
    {
        if (result.RollbackRef is null) return new ToolVerification(false, result.Outcome);
        var (ok, bad) = await s.Executor.VerifyAsync(result.RollbackRef, ct);
        var expected = result.ObservedState?["moved"]?.GetValue<int>() ?? 0;
        return bad == 0 && ok == expected
            ? new ToolVerification(true, result.Outcome)
            : new ToolVerification(false, ToolOutcome.Partial, $"{ok} move(s) verified, {bad} mismatch(es).");
    }
}

public sealed class UndoOrganizationTool(FileToolServices s) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "files_undo_organization",
        "Undo the most recent (or a specified) file organization. Only restores files that are unchanged and whose original location is still free; never overwrites.",
        Schema.Object(("manifestId", Schema.Str("Manifest to undo; omit for the most recent", "^m-[0-9a-f]{12}$", 14), false)),
        RiskTier.Moderate, true);

    public override async ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var id = Str(args, "manifestId");
        if (string.IsNullOrEmpty(id)) id = await ctx.Journal.GetLastUndoableManifestAsync(ct) ?? "";
        if (string.IsNullOrEmpty(id)) return ToolValidation.Invalid("nothing_to_undo", "There is no organization to undo.");
        var plan = await s.Executor.PlanUndoAsync(id, ct);
        if (plan.Count == 0) return ToolValidation.Invalid("nothing_to_undo", "Nothing from that run can be undone.");
        ctx.Items["manifestId"] = id;
        var ok = plan.Count(p => p.Conflict is null);
        var lines = new List<string> { $"{ok} file(s) will return to their original location." };
        foreach (var g in plan.Where(p => p.Conflict is not null).GroupBy(p => p.Conflict)) lines.Add($"{g.Count()} will stay where they are ({g.Key}).");
        var digest = OrganizationPlanner.ComputeDigest("undo:" + id, plan.Select(p => new OrganizeItem(p.Move.DestinationPath, p.Move.SourcePath, p.Conflict ?? "ok", p.Move.Length, p.Move.LastWriteUtc)));
        return ToolValidation.NeedsApproval(new ApprovalRequest(ctx.Auth.RunId, Definition.Name, StandingRuleOperations.ReversibleSameRootMove, digest, $"Undo organization ({ok} file(s))", lines, RiskTier.Moderate));
    }

    public override async ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var id = ctx.Items.TryGetValue("manifestId", out var v) ? (string)v : Str(args, "manifestId");
        var moves = await ctx.Journal.GetMovesAsync(id, ct);
        if (moves.Count == 0) return ToolResult.Fail("nothing_to_undo", "Nothing to undo.");
        // The allowed area for undo is exactly where this run's files came from and went to (moves can cross folders).
        var root = ManifestMoveToolBase.CommonAncestor(moves.SelectMany(m => new[] { m.SourcePath, m.DestinationPath }));
        var r = await s.Executor.UndoAsync(id, s.PolicyFor(root), ctx, ct);
        var state = new JsonObject { ["restored"] = r.Moved, ["conflicts"] = r.Skipped, ["failed"] = r.Failed };
        var outcome = r.Skipped + r.Failed == 0 ? ToolOutcome.Success : r.Moved > 0 ? ToolOutcome.Partial : ToolOutcome.Failure;
        return new ToolResult(outcome, state, null, $"Okay, I put {r.Moved} file{(r.Moved == 1 ? "" : "s")} back where they were." + (r.Skipped > 0 ? $" I left {r.Skipped} alone because they were edited or something new is in their old spot." : ""), false, false, r.MovedDestinations, null, TimeSpan.Zero);
    }

    public override ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct)
    {
        var all = result.ChangedResources.All(File.Exists);
        return ValueTask.FromResult(new ToolVerification(all, all ? result.Outcome : ToolOutcome.Partial));
    }
}

public sealed class PreviewTempCleanupTool(FileToolServices s) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "cleanup_preview_temp",
        "Measure the user's temporary-files folder: eligible (older than 24h, not in use, not links/system) file count and estimated bytes. Also reports current free space. No changes.",
        Schema.Object(), RiskTier.Read, false);

    public override ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var temp = s.Folders.Resolve(KnownFolder.Temp);
        if (temp is null) return ValueTask.FromResult(ToolResult.Fail("folder_unavailable", "Temp folder not found."));
        var policy = new PathPolicy([temp]) { AllowHidden = true };
        var m = s.Cleaner.Preview([temp], policy);
        s.Manifests.Put(m);
        var summary = TempCleaner.Summary(m);
        var drive = s.Drives.Get(DriveLetterOf(temp));
        if (drive is not null) summary["driveFreeBytes"] = drive.AvailableToUserBytes;
        return ValueTask.FromResult(ToolResult.Ok(summary, m.Items.Count == 0 ? "Your temp folder is already clean." : $"I found {m.Items.Count} old temporary files, {Bytes.Spoken(m.EligibleBytes)} in total.", true));
    }

    internal static string DriveLetterOf(string path) => OperatingSystem.IsWindows() && path.Length > 1 && path[1] == ':' ? path[..1] : "C";
}

public sealed class ApplyTempCleanupTool(FileToolServices s) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "cleanup_apply_temp",
        "Remove the eligible temporary files from a cleanup preview. Requires explicit local confirmation every time. Reports measured free space before and after.",
        Schema.Object(
            ("manifestId", Schema.Str("manifestId from cleanup_preview_temp", "^c-[0-9a-f]{12}$", 14), true),
            ("mode", Schema.Str("permanent frees space now; recycle keeps files recoverable but frees no space until the bin is emptied", null, 10, "permanent", "recycle"), true)),
        RiskTier.Moderate, true);

    public override ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var m = s.Manifests.GetCleanup(Str(args, "manifestId"));
        if (m is null) return ValueTask.FromResult(ToolValidation.Invalid("manifest_unknown", "Preview expired. Preview again."));
        if (m.Items.Count == 0) return ValueTask.FromResult(ToolValidation.Invalid("nothing_to_do", "No eligible temporary files."));
        var recycle = Str(args, "mode") == "recycle";
        if (recycle && !s.Remover.SupportsRecycle) return ValueTask.FromResult(ToolValidation.Invalid("recycle_unavailable", "Recycle Bin is unavailable."));
        var lines = new List<string>
        {
            $"Location: {string.Join(", ", m.Roots)}",
            $"{m.Items.Count} file(s), about {Bytes.Format(m.EligibleBytes)} (estimate)",
            recycle ? "Files go to the Recycle Bin. Disk space is NOT freed until you empty it." : "Files are deleted permanently. This cannot be undone.",
            "Files in use, recent (<24h), links and system files are skipped. Downloads and browser data are never touched.",
        };
        return ValueTask.FromResult(ToolValidation.NeedsApproval(new ApprovalRequest(ctx.Auth.RunId, Definition.Name, recycle ? "files.cleanup_temp_recycle" : "files.cleanup_temp_permanent",
            m.Digest, recycle ? "Move temporary files to the Recycle Bin" : "Permanently delete temporary files", lines, RiskTier.Moderate)));
    }

    public override ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var m = s.Manifests.GetCleanup(Str(args, "manifestId"));
        if (m is null) return ValueTask.FromResult(ToolResult.Fail("manifest_unknown", "Preview expired."));
        if (ctx.Approval is null || ctx.Approval.ManifestDigest != m.Digest) return ValueTask.FromResult(ToolResult.Fail("approval_mismatch", "Approval does not match."));
        s.Manifests.Consume(m.ManifestId);
        var recycle = Str(args, "mode") == "recycle";
        var letter = PreviewTempCleanupTool.DriveLetterOf(m.Roots[0]);
        var before = s.Drives.Get(letter)?.AvailableToUserBytes;
        var policy = new PathPolicy(m.Roots) { AllowHidden = true };
        var progress = new VoxStride.Core.Events.SyncProgress<(int, int)>(p => ctx.Events.Publish(ctx.Auth.RunId, AgentState.Executing, "cleanup", $"Processed {p.Item1} of {p.Item2}", ctx.StepId, p.Item1, p.Item2));
        var r = s.Cleaner.Apply(m, policy, s.Remover, recycle, ctx, progress, ct);
        var after = s.Drives.Get(letter)?.AvailableToUserBytes;
        var state = new JsonObject
        {
            ["removedFiles"] = r.Removed, ["removedBytesFromFileSizes"] = r.RemovedBytes, ["skipped"] = r.Skipped, ["mode"] = recycle ? "recycle" : "permanent",
            ["freeBefore"] = before, ["freeAfter"] = after,
            ["measuredFreeDelta"] = before.HasValue && after.HasValue ? after - before : null,
        };
        var delta = before.HasValue && after.HasValue ? after.Value - before.Value : (long?)null;
        var msg = recycle
            ? $"I moved {r.Removed} temp files to the Recycle Bin. You'll get the space back once you empty it."
            : delta is > 0 ? $"All done — I cleared {r.Removed} temp files and freed up {Bytes.Spoken(delta.Value)}." : $"I cleared {r.Removed} temp files.";
        if (r.Skipped > 0) msg += $" {r.Skipped} were in use, so I left them.";
        var outcome = r.Removed == 0 ? ToolOutcome.Failure : r.Skipped > 0 ? ToolOutcome.Partial : ToolOutcome.Success;
        return ValueTask.FromResult(new ToolResult(outcome, state, null, msg, false, false, [], null, TimeSpan.Zero));
    }

    public override ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(new ToolVerification(result.ObservedState?["freeAfter"] is not null, result.Outcome));
}
