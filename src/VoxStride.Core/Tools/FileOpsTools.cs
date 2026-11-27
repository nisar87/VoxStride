using System.Diagnostics;
using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Execution;
using VoxStride.Core.Files;
using VoxStride.Core.Json;
using VoxStride.Core.Policy;

namespace VoxStride.Core.Tools;

/// <summary>
/// The folders VoxStride's general file tools may touch. Defaults to the user's Desktop, Documents, Downloads,
/// Pictures, Music and Videos (resolved from Windows known folders, so OneDrive redirection works); the user can add
/// or remove folders under Settings › Permissions. Everything else — Windows, Program Files, other users, system and
/// hidden items, links — is refused by <see cref="PathPolicy"/>.
/// </summary>
public sealed class FileScope(IKnownFolders folders, Func<IReadOnlyList<string>>? extraRoots = null, Func<IReadOnlyCollection<KnownFolder>>? disabled = null)
{
    public static readonly KnownFolder[] DefaultFolders = [KnownFolder.Desktop, KnownFolder.Documents, KnownFolder.Downloads, KnownFolder.Pictures, KnownFolder.Music, KnownFolder.Videos];

    public IReadOnlyList<string> Roots
    {
        get
        {
            var off = disabled?.Invoke() ?? [];
            var list = DefaultFolders.Where(f => !off.Contains(f)).Select(folders.Resolve).OfType<string>().Where(Directory.Exists).ToList();
            foreach (var r in extraRoots?.Invoke() ?? []) if (Directory.Exists(r)) list.Add(r);
            return list;
        }
    }

    public PathPolicy Policy() => new(Roots);

    /// <summary>
    /// Resolves "desktop", "documents\\reports", "Downloads/x.pdf" or an absolute path to a canonical path inside the scope.
    /// </summary>
    public bool TryResolve(string input, out string path, out string error)
    {
        path = ""; error = "";
        if (string.IsNullOrWhiteSpace(input)) { error = "No path was given."; return false; }
        var s = input.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        var first = s.Split(Path.DirectorySeparatorChar, 2);
        if (!Path.IsPathRooted(s) && Enum.TryParse<KnownFolder>(first[0], true, out var kf) && kf != KnownFolder.Temp && folders.Resolve(kf) is { } basePath)
            s = first.Length > 1 ? Path.Combine(basePath, first[1]) : basePath;
        else if (!Path.IsPathRooted(s)) { error = $"'{input}' isn't a full path. Start it with a known folder like Desktop, Documents or Downloads."; return false; }
        var decision = Policy().Check(s, out var canonical);
        if (decision != PathDecision.Allowed)
        {
            error = decision switch
            {
                PathDecision.OutsideRoots => $"'{input}' is outside the folders VoxStride may use. You can allow more folders under Settings › Permissions.",
                PathDecision.Protected or PathDecision.System => $"'{input}' is a protected system location.",
                PathDecision.Hidden => $"'{input}' is hidden, so I leave it alone.",
                PathDecision.ReparsePoint => $"'{input}' is a link or junction, which I don't follow for safety.",
                PathDecision.Network => "Network paths aren't allowed.",
                _ => $"'{input}' isn't a valid path.",
            };
            return false;
        }
        path = canonical;
        return true;
    }
}

public abstract class FileOpsToolBase(FileScope scope) : ToolBase
{
    protected FileScope Scope { get; } = scope;

    protected static IReadOnlyList<string> Strings(JsonObject a, string k) =>
        a[k] is JsonArray arr ? arr.Select(x => x!.GetValue<string>()).ToList() : [];

    protected static JsonObject Entry(FileSystemInfo f, string root) => new()
    {
        ["name"] = f.Name,
        ["path"] = f.FullName,
        ["relative"] = Path.GetRelativePath(root, f.FullName),
        ["type"] = f is DirectoryInfo ? "folder" : "file",
        ["sizeBytes"] = f is FileInfo fi ? fi.Length : null,
        ["modified"] = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
    };

    protected static bool IsSkippable(FileSystemInfo f) =>
        (f.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0 || f.Name.StartsWith('.');

    protected static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}

public sealed class ListFilesTool(FileScope scope) : FileOpsToolBase(scope)
{
    public override ToolDefinition Definition { get; } = new(
        "files_list",
        "List the files and folders directly inside a folder (not recursive). Path may start with a known folder name: Desktop, Documents, Downloads, Pictures, Music, Videos. Hidden/system items are omitted. No changes.",
        Schema.Object(
            ("path", Schema.Str("Folder, e.g. 'Desktop' or 'Documents\\\\Invoices'", null, 400), true),
            ("pattern", Schema.Str("Optional wildcard filter, e.g. '*.pdf'", @"^[^\\/:""<>|]{1,60}$", 60), false),
            ("max", Schema.Int("Maximum entries to return (default 100)", 1, 500), false)),
        RiskTier.Read, false);

    public override ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (!Scope.TryResolve(Str(args, "path"), out var dir, out var err)) return ValueTask.FromResult(ToolResult.Fail("path_not_allowed", err));
        if (!Directory.Exists(dir)) return ValueTask.FromResult(ToolResult.Fail("not_found", $"I couldn't find the folder {Path.GetFileName(dir)}."));
        var max = Int(args, "max", 100);
        var pattern = Str(args, "pattern", "*");
        var all = new DirectoryInfo(dir).EnumerateFileSystemInfos(pattern, new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint })
            .Where(f => !IsSkippable(f)).OrderBy(f => f is FileInfo).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var items = new JsonArray(all.Take(max).Select(f => (JsonNode)Entry(f, dir)).ToArray());
        int files = all.Count(f => f is FileInfo), folders = all.Count - files;
        var state = new JsonObject { ["folder"] = dir, ["files"] = files, ["folders"] = folders, ["returned"] = items.Count, ["truncated"] = all.Count > max, ["items"] = items };
        return ValueTask.FromResult(ToolResult.Ok(state, all.Count == 0 ? $"{Path.GetFileName(dir)} is empty." : $"{Path.GetFileName(dir)} has {Plural(files, "file")} and {Plural(folders, "folder")}.", true));
    }
}

public sealed class SearchFilesTool(FileScope scope) : FileOpsToolBase(scope)
{
    public const int MaxEntriesScanned = 60_000;
    public static readonly TimeSpan MaxScanTime = TimeSpan.FromSeconds(6);

    public override ToolDefinition Definition { get; } = new(
        "files_search",
        "Find files by name and/or type inside the allowed folders (recursive, bounded). Omit 'in' to search all allowed folders. Returns matches with path, size and date. No changes.",
        Schema.Object(
            ("in", Schema.Str("Folder to search, e.g. 'Documents' (default: all allowed folders)", null, 400), false),
            ("nameContains", Schema.Str("Text the file name must contain (case-insensitive)", null, 100), false),
            ("extensions", Schema.Array("File extensions with dot, e.g. .pdf", Schema.Str("Extension", @"^\.[A-Za-z0-9]{1,10}$", 11), 20), false),
            ("modifiedWithinDays", Schema.Int("Only files changed in the last N days", 1, 3650), false),
            ("minSizeMb", Schema.Int("Only files at least this many MB", 1, 1_000_000), false),
            ("includeFolders", Schema.Bool("Also match folder names (default false)"), false),
            ("max", Schema.Int("Maximum results (default 50)", 1, 300), false)),
        RiskTier.Read, false);

    public override ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Str(args, "nameContains")) && Strings(args, "extensions").Count == 0 && args["modifiedWithinDays"] is null && args["minSizeMb"] is null)
            return ValueTask.FromResult(ToolValidation.Invalid("too_broad", "Give a name, type, date or size to search for."));
        return ValueTask.FromResult(ToolValidation.Valid());
    }

    public override ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        IReadOnlyList<string> roots;
        if (args["in"] is not null)
        {
            if (!Scope.TryResolve(Str(args, "in"), out var r, out var err)) return ValueTask.FromResult(ToolResult.Fail("path_not_allowed", err));
            roots = [r];
        }
        else roots = Scope.Roots;
        var name = Str(args, "nameContains");
        var exts = new HashSet<string>(Strings(args, "extensions"), StringComparer.OrdinalIgnoreCase);
        var since = args["modifiedWithinDays"] is null ? (DateTime?)null : DateTime.Now.AddDays(-Int(args, "modifiedWithinDays", 30));
        var minBytes = args["minSizeMb"] is null ? 0L : Int(args, "minSizeMb", 0) * 1_048_576L;
        var folders = Bool(args, "includeFolders", false);
        var max = Int(args, "max", 50);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 10, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        var sw = Stopwatch.StartNew();
        var scanned = 0;
        var hits = new List<(FileSystemInfo f, string root)>();
        var stoppedEarly = false;
        foreach (var root in roots)
        {
            foreach (var f in new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (++scanned > MaxEntriesScanned || sw.Elapsed > MaxScanTime) { stoppedEarly = true; break; }
                if (IsSkippable(f) || PathPolicy.IsProtectedLocation(f.FullName)) continue;
                if (f is DirectoryInfo && !folders) continue;
                if (name.Length > 0 && !f.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (exts.Count > 0 && (f is DirectoryInfo || !exts.Contains(f.Extension))) continue;
                if (since is { } s && f.LastWriteTime < s) continue;
                if (minBytes > 0 && (f is not FileInfo fi || fi.Length < minBytes)) continue;
                hits.Add((f, root));
                if (hits.Count >= max) { stoppedEarly = true; break; }
            }
            if (stoppedEarly) break;
        }
        var items = new JsonArray(hits.OrderByDescending(h => h.f.LastWriteTime).Select(h => (JsonNode)Entry(h.f, h.root)).ToArray());
        var state = new JsonObject { ["matches"] = hits.Count, ["scanned"] = scanned, ["searched"] = new JsonArray(roots.Select(r => (JsonNode)r!).ToArray()), ["incomplete"] = stoppedEarly, ["items"] = items };
        var msg = hits.Count == 0 ? "I didn't find any matching files." : $"I found {Plural(hits.Count, "match")}" + (stoppedEarly ? " (I stopped there; narrow the search for more)." : ".");
        Log.Info("tool", $"files_search scanned={scanned} matches={hits.Count} ms={sw.ElapsedMilliseconds} incomplete={stoppedEarly}");
        return ValueTask.FromResult(ToolResult.Ok(state, msg, true));
    }
}

public sealed class CreateFolderTool(FileScope scope, IExecutionJournal journal) : FileOpsToolBase(scope)
{
    public override ToolDefinition Definition { get; } = new(
        "files_create_folder",
        "Create a new folder inside an allowed folder. Never replaces anything; does nothing if it already exists.",
        Schema.Object(
            ("parent", Schema.Str("Where to create it, e.g. 'Desktop' or 'Documents\\\\Projects'", null, 400), true),
            ("name", Schema.Str("New folder name", @"^[^\\/:*?""<>|]{1,120}$", 120), true)),
        RiskTier.LowReversible, true);

    public override ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (!Scope.TryResolve(Str(args, "parent"), out var parent, out var err)) return ValueTask.FromResult(ToolValidation.Invalid("path_not_allowed", err));
        if (!Directory.Exists(parent)) return ValueTask.FromResult(ToolValidation.Invalid("not_found", "The parent folder doesn't exist."));
        var name = Str(args, "name").Trim().TrimEnd('.');
        if (name.Length == 0 || IsReservedName(name)) return ValueTask.FromResult(ToolValidation.Invalid("invalid_name", "That isn't a valid folder name."));
        if (!Scope.TryResolve(Path.Combine(parent, name), out var target, out err)) return ValueTask.FromResult(ToolValidation.Invalid("path_not_allowed", err));
        ctx.Items["target"] = target;
        return ValueTask.FromResult(ToolValidation.Valid());
    }

    public override async ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var target = (string)ctx.Items["target"];
        if (File.Exists(target)) return ToolResult.Fail("exists_as_file", "A file with that name already exists there.");
        if (Directory.Exists(target)) return ToolResult.Ok(new JsonObject { ["path"] = target, ["created"] = false }, $"The folder {Path.GetFileName(target)} already exists.", true);
        Directory.CreateDirectory(target);
        await journal.RecordCreatedFolderAsync(new CreatedFolderRecord(ctx.Auth.RunId, "f-" + ctx.Auth.RunId.ToString("N")[..12], target), CancellationToken.None);
        return ToolResult.Ok(new JsonObject { ["path"] = target, ["created"] = true }, $"I created the folder {Path.GetFileName(target)}.", false, [target]);
    }

    public override ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(new ToolVerification(Directory.Exists((string)ctx.Items["target"]), result.Outcome));

    internal static bool IsReservedName(string n)
    {
        var stem = Path.GetFileNameWithoutExtension(n).ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]));
    }
}

/// <summary>
/// Moves (and renames) build an exact manifest during validation, the user approves that exact list on screen, and
/// <see cref="MoveExecutor"/> applies it: never overwriting, re-checking each file just before moving, verifying,
/// and journaling every move so "undo that" (files_undo_organization) can put things back.
/// </summary>
public abstract class ManifestMoveToolBase(FileScope scope, FileToolServices services) : FileOpsToolBase(scope)
{
    protected FileToolServices Services { get; } = services;
    public const string Operation = "files.move_batch";

    protected ToolValidation Approve(ToolContext ctx, string root, List<OrganizeItem> items, List<SkippedItem> skipped, string title, string verb)
    {
        if (items.Count == 0)
            return ToolValidation.Invalid("nothing_to_do", skipped.Count > 0 ? $"Nothing can be {verb}: " + string.Join("; ", skipped.Take(3).Select(x => $"{Path.GetFileName(x.Path)} ({x.Reason})")) : "Nothing to do.");
        var id = "m-" + Guid.NewGuid().ToString("N")[..12];
        var m = new OrganizationManifest(id, root, items, skipped, items.Select(i => Path.GetDirectoryName(i.DestinationPath)!).Distinct().Where(d => !Directory.Exists(d)).ToList(),
            OrganizationPlanner.ComputeDigest(root, items), DateTimeOffset.UtcNow);
        Services.Manifests.Put(m);
        ctx.Items["manifestId"] = id;
        var lines = items.Take(30).Select(i => $"{i.SourcePath}  →  {i.DestinationPath}").ToList();
        if (items.Count > 30) lines.Add($"…and {items.Count - 30} more");
        foreach (var s in skipped.Take(10)) lines.Add($"Left alone: {Path.GetFileName(s.Path)} ({s.Reason})");
        lines.Add("Nothing is overwritten. You can say \"undo that\" afterwards.");
        return ToolValidation.NeedsApproval(new ApprovalRequest(ctx.Auth.RunId, Definition.Name, Operation, m.Digest, title, lines, RiskTier.Moderate, Scope: root));
    }

    protected static string CommonRoot(IEnumerable<string> paths) => CommonAncestor(paths);

    public static string CommonAncestor(IEnumerable<string> paths)
    {
        var list = paths.Select(p => Path.GetDirectoryName(p) ?? p).ToList();
        var common = list[0];
        foreach (var p in list.Skip(1))
            while (!PathPolicy.IsUnder(p, common) && Path.GetDirectoryName(common) is { } up) common = up;
        return common;
    }

    public override async ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var m = Services.Manifests.GetOrganization((string)ctx.Items["manifestId"]);
        if (m is null) return ToolResult.Fail("manifest_unknown", "The plan expired. Ask again.");
        if (ctx.Approval is null || ctx.Approval.ManifestDigest != m.Digest) return ToolResult.Fail("approval_mismatch", "Approval does not match this plan.");
        Services.Manifests.Consume(m.ManifestId);
        var r = await Services.Executor.ApplyAsync(m, Scope.Policy(), ctx, null, ct);
        var state = new JsonObject { ["manifestId"] = m.ManifestId, ["done"] = r.Moved, ["skipped"] = r.Skipped, ["failed"] = r.Failed, ["unknown"] = r.Unknown, ["undoAvailable"] = r.Moved > 0 };
        var outcome = r.Failed + r.Unknown + r.Skipped == 0 ? ToolOutcome.Success : r.Moved > 0 ? ToolOutcome.Partial : ToolOutcome.Failure;
        return new ToolResult(outcome, state, null, Summary(r, m.Items.Count), false, false, r.MovedDestinations, m.ManifestId, TimeSpan.Zero);
    }

    protected abstract string Summary(MoveBatchResult r, int planned);

    public override async ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct)
    {
        if (result.RollbackRef is null) return new ToolVerification(false, result.Outcome);
        var (ok, bad) = await Services.Executor.VerifyAsync(result.RollbackRef, ct);
        var expected = result.ObservedState?["done"]?.GetValue<int>() ?? 0;
        return bad == 0 && ok == expected ? new ToolVerification(true, result.Outcome) : new ToolVerification(false, ToolOutcome.Partial, $"{ok} verified, {bad} mismatch(es).");
    }
}

public sealed class MoveFilesTool(FileScope scope, FileToolServices services) : ManifestMoveToolBase(scope, services)
{
    public override ToolDefinition Definition { get; } = new(
        "files_move",
        "Move files (or folders) into a destination folder within the allowed folders. The user approves the exact list on screen. Never overwrites; undoable with files_undo_organization.",
        Schema.Object(
            ("sources", Schema.Array("Files or folders to move (full paths or known-folder relative)", Schema.Str("Path", null, 400), 100), true),
            ("destination", Schema.Str("Destination folder (created if missing)", null, 400), true)),
        RiskTier.Moderate, true);

    public override ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (!Scope.TryResolve(Str(args, "destination"), out var dest, out var err)) return ValueTask.FromResult(ToolValidation.Invalid("path_not_allowed", err));
        if (File.Exists(dest)) return ValueTask.FromResult(ToolValidation.Invalid("destination_is_file", "The destination is a file, not a folder."));
        var items = new List<OrganizeItem>();
        var skipped = new List<SkippedItem>();
        foreach (var raw in Strings(args, "sources"))
        {
            if (!Scope.TryResolve(raw, out var src, out var e)) { skipped.Add(new SkippedItem(raw, e)); continue; }
            if (Directory.Exists(src))
            {
                if (PathPolicy.IsUnder(dest, src)) { skipped.Add(new SkippedItem(src, "can't move a folder into itself")); continue; }
                skipped.Add(new SkippedItem(src, "moving whole folders isn't supported yet; move the files inside it"));
                continue;
            }
            var fi = new FileInfo(src);
            if (!fi.Exists) { skipped.Add(new SkippedItem(src, "not found")); continue; }
            var target = Path.Combine(dest, fi.Name);
            if (string.Equals(Path.GetDirectoryName(src), dest, StringComparison.OrdinalIgnoreCase)) { skipped.Add(new SkippedItem(src, "already there")); continue; }
            if (File.Exists(target) || Directory.Exists(target)) { skipped.Add(new SkippedItem(src, "a file with the same name is already in the destination")); continue; }
            if (items.Any(i => string.Equals(i.DestinationPath, target, StringComparison.OrdinalIgnoreCase))) { skipped.Add(new SkippedItem(src, "two files would get the same name")); continue; }
            items.Add(new OrganizeItem(fi.FullName, target, "move", fi.Length, fi.LastWriteTimeUtc));
        }
        var root = items.Count > 0 ? CommonRoot(items.Select(i => i.SourcePath).Append(Path.Combine(dest, "x"))) : dest;
        return ValueTask.FromResult(Approve(ctx, root, items, skipped, $"Move {Plural(items.Count, "file")} to {Path.GetFileName(dest)}", "moved"));
    }

    protected override string Summary(MoveBatchResult r, int planned) =>
        r.Moved == 0 ? "I couldn't move any of those files — they changed or were in use, so I left everything as it was."
            : $"I moved {r.Moved} of {planned} file{(planned == 1 ? "" : "s")}." + (r.Moved < planned ? $" {planned - r.Moved} couldn't be moved." : "") + " Say \"undo that\" to put them back.";
}

public sealed class RenameFileTool(FileScope scope, FileToolServices services) : ManifestMoveToolBase(scope, services)
{
    public override ToolDefinition Definition { get; } = new(
        "files_rename",
        "Rename one file in place (same folder). The user approves on screen. Never overwrites; undoable with files_undo_organization.",
        Schema.Object(
            ("path", Schema.Str("The file to rename", null, 400), true),
            ("newName", Schema.Str("New file name including extension", @"^[^\\/:*?""<>|]{1,200}$", 200), true)),
        RiskTier.Moderate, true);

    public override ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (!Scope.TryResolve(Str(args, "path"), out var src, out var err)) return ValueTask.FromResult(ToolValidation.Invalid("path_not_allowed", err));
        var fi = new FileInfo(src);
        if (!fi.Exists) return ValueTask.FromResult(ToolValidation.Invalid("not_found", "I couldn't find that file."));
        var name = Str(args, "newName").Trim().TrimEnd('.');
        if (name.Length == 0 || CreateFolderTool.IsReservedName(name)) return ValueTask.FromResult(ToolValidation.Invalid("invalid_name", "That isn't a valid file name."));
        if (!string.Equals(Path.GetExtension(name), fi.Extension, StringComparison.OrdinalIgnoreCase) && Path.GetExtension(name).Length == 0) name += fi.Extension; // keep the type unless asked
        var target = Path.Combine(fi.DirectoryName!, name);
        if (string.Equals(target, src, StringComparison.Ordinal)) return ValueTask.FromResult(ToolValidation.Invalid("same_name", "It already has that name."));
        var skipped = new List<SkippedItem>();
        var items = new List<OrganizeItem>();
        if ((File.Exists(target) || Directory.Exists(target)) && !string.Equals(target, src, StringComparison.OrdinalIgnoreCase)) skipped.Add(new SkippedItem(src, $"'{name}' already exists there"));
        else items.Add(new OrganizeItem(src, target, "rename", fi.Length, fi.LastWriteTimeUtc));
        return ValueTask.FromResult(Approve(ctx, fi.DirectoryName!, items, skipped, $"Rename {fi.Name} to {name}", "renamed"));
    }

    protected override string Summary(MoveBatchResult r, int planned) =>
        r.Moved == 1 ? $"Renamed it to {Path.GetFileName(r.MovedDestinations[0])}. Say \"undo that\" to change it back." : "I couldn't rename it — it changed or is open in another app.";
}

public sealed class CopyFilesTool(FileScope scope) : FileOpsToolBase(scope)
{
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    public override ToolDefinition Definition { get; } = new(
        "files_copy",
        "Copy files into a destination folder within the allowed folders. Originals are untouched; nothing is overwritten (existing names are skipped). Total size limit 2 GB.",
        Schema.Object(
            ("sources", Schema.Array("Files to copy", Schema.Str("Path", null, 400), 100), true),
            ("destination", Schema.Str("Destination folder (created if missing)", null, 400), true)),
        RiskTier.LowReversible, true);

    public override ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (!Scope.TryResolve(Str(args, "destination"), out var dest, out var err)) return ValueTask.FromResult(ToolValidation.Invalid("path_not_allowed", err));
        if (File.Exists(dest)) return ValueTask.FromResult(ToolValidation.Invalid("destination_is_file", "The destination is a file, not a folder."));
        var plan = new List<(string src, string dst, long len)>();
        var skipped = new JsonArray();
        foreach (var raw in Strings(args, "sources"))
        {
            if (!Scope.TryResolve(raw, out var src, out var e) || !File.Exists(src)) { skipped.Add(new JsonObject { ["path"] = raw, ["reason"] = e.Length > 0 ? e : "not a file" }); continue; }
            var dst = Path.Combine(dest, Path.GetFileName(src));
            if (File.Exists(dst) || Directory.Exists(dst) || plan.Any(p => string.Equals(p.dst, dst, StringComparison.OrdinalIgnoreCase))) { skipped.Add(new JsonObject { ["path"] = src, ["reason"] = "name already exists in destination" }); continue; }
            plan.Add((src, dst, new FileInfo(src).Length));
        }
        if (plan.Count == 0) return ValueTask.FromResult(ToolValidation.Invalid("nothing_to_do", "There's nothing to copy (missing files or names already in the destination)."));
        if (plan.Sum(p => p.len) > MaxTotalBytes) return ValueTask.FromResult(ToolValidation.Invalid("too_large", "That's more than 2 GB; copy it in File Explorer instead."));
        ctx.Items["plan"] = plan;
        ctx.Items["dest"] = dest;
        ctx.Items["skipped"] = skipped;
        return ValueTask.FromResult(ToolValidation.Valid());
    }

    public override ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var plan = (List<(string src, string dst, long len)>)ctx.Items["plan"];
        Directory.CreateDirectory((string)ctx.Items["dest"]);
        var copied = new List<string>();
        var failed = new JsonArray();
        foreach (var (src, dst, len) in plan)
        {
            ctx.EnsureCanWrite(ct);
            try
            {
                File.Copy(src, dst, overwrite: false);
                if (new FileInfo(dst).Length == len) copied.Add(dst); else failed.Add(new JsonObject { ["path"] = src, ["reason"] = "size mismatch after copy" });
            }
            catch (IOException ex) { failed.Add(new JsonObject { ["path"] = src, ["reason"] = ex.Message }); }
            catch (UnauthorizedAccessException) { failed.Add(new JsonObject { ["path"] = src, ["reason"] = "access denied" }); }
        }
        var state = new JsonObject { ["copied"] = copied.Count, ["failed"] = failed, ["skipped"] = ((JsonArray)ctx.Items["skipped"]).DeepClone(), ["destinations"] = new JsonArray(copied.Select(c => (JsonNode)c!).ToArray()) };
        var outcome = copied.Count == plan.Count ? ToolOutcome.Success : copied.Count > 0 ? ToolOutcome.Partial : ToolOutcome.Failure;
        var msg = copied.Count == 0 ? "I couldn't copy those files." : $"I copied {Plural(copied.Count, "file")} to {Path.GetFileName((string)ctx.Items["dest"])}." + (failed.Count > 0 ? $" {failed.Count} failed." : "");
        return ValueTask.FromResult(new ToolResult(outcome, state, null, msg, false, false, copied, null, TimeSpan.Zero));
    }

    public override ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct)
    {
        var plan = (List<(string src, string dst, long len)>)ctx.Items["plan"];
        var ok = result.ChangedResources.All(d => plan.Any(p => p.dst == d && File.Exists(d) && File.Exists(p.src) && new FileInfo(d).Length == p.len));
        return ValueTask.FromResult(new ToolVerification(ok, ok ? result.Outcome : ToolOutcome.Partial));
    }
}

public sealed class RecycleFilesTool(FileScope scope, IFileRemover remover) : FileOpsToolBase(scope)
{
    public override ToolDefinition Definition { get; } = new(
        "files_recycle",
        "Send files or folders to the Windows Recycle Bin (recoverable from the Recycle Bin). Never deletes permanently. Requires the user's on-screen approval of the exact list every time.",
        Schema.Object(("paths", Schema.Array("Files or folders to recycle", Schema.Str("Path", null, 400), 100), true)),
        RiskTier.Moderate, true);

    public override ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (!remover.SupportsRecycle) return ValueTask.FromResult(ToolValidation.Invalid("recycle_unavailable", "The Recycle Bin isn't available."));
        var roots = Scope.Roots;
        var items = new List<(string path, bool dir, long size, DateTime written)>();
        var lines = new List<string>();
        foreach (var raw in Strings(args, "paths"))
        {
            if (!Scope.TryResolve(raw, out var p, out var e)) { lines.Add($"Refused: {raw} ({e})"); continue; }
            if (roots.Any(r => string.Equals(PathPolicy.Normalize(r), p, StringComparison.OrdinalIgnoreCase))) { lines.Add($"Refused: {p} (a whole known folder can't be recycled)"); continue; }
            if (File.Exists(p)) { var fi = new FileInfo(p); items.Add((p, false, fi.Length, fi.LastWriteTimeUtc)); }
            else if (Directory.Exists(p)) { var di = new DirectoryInfo(p); items.Add((p, true, -1, di.LastWriteTimeUtc)); }
            else lines.Add($"Not found: {raw}");
        }
        if (items.Count == 0) return ValueTask.FromResult(ToolValidation.Invalid("nothing_to_do", lines.Count > 0 ? string.Join(" ", lines) : "Nothing to recycle."));
        ctx.Items["items"] = items;
        var digest = OrganizationPlanner.ComputeDigest("recycle", items.Select(i => new OrganizeItem(i.path, "$Recycle", i.dir ? "folder" : "file", i.size, i.written)));
        var display = items.Take(30).Select(i => (i.dir ? "Folder: " : "") + i.path).Concat(lines).ToList();
        if (items.Count > 30) display.Add($"…and {items.Count - 30} more");
        display.Add("They go to the Recycle Bin, where you can restore them.");
        return ValueTask.FromResult(ToolValidation.NeedsApproval(new ApprovalRequest(ctx.Auth.RunId, Definition.Name, "files.recycle", digest, $"Move {Plural(items.Count, "item")} to the Recycle Bin", display, RiskTier.Moderate)));
    }

    public override ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var items = (List<(string path, bool dir, long size, DateTime written)>)ctx.Items["items"];
        var done = new List<string>();
        var problems = new JsonArray();
        var policy = Scope.Policy();
        foreach (var i in items)
        {
            ctx.EnsureCanWrite(ct);
            // Re-check just before the effect: unchanged and still allowed.
            if (policy.Check(i.path, out _) != PathDecision.Allowed) { problems.Add(new JsonObject { ["path"] = i.path, ["reason"] = "no longer allowed" }); continue; }
            if (!i.dir && (!File.Exists(i.path) || new FileInfo(i.path).Length != i.size || new FileInfo(i.path).LastWriteTimeUtc != i.written)) { problems.Add(new JsonObject { ["path"] = i.path, ["reason"] = "changed since you approved" }); continue; }
            try { remover.Remove(i.path, recycle: true); done.Add(i.path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add(new JsonObject { ["path"] = i.path, ["reason"] = ex.Message }); }
        }
        var outcome = done.Count == items.Count ? ToolOutcome.Success : done.Count > 0 ? ToolOutcome.Partial : ToolOutcome.Failure;
        var msg = done.Count == 0 ? "I couldn't move those to the Recycle Bin." : $"I moved {Plural(done.Count, "item")} to the Recycle Bin. You can restore them from there." + (problems.Count > 0 ? $" {problems.Count} couldn't be moved." : "");
        return ValueTask.FromResult(new ToolResult(outcome, new JsonObject { ["recycled"] = done.Count, ["problems"] = problems }, null, msg, false, false, done, null, TimeSpan.Zero));
    }

    public override ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct)
    {
        var gone = result.ChangedResources.All(p => !File.Exists(p) && !Directory.Exists(p));
        return ValueTask.FromResult(new ToolVerification(gone, gone ? result.Outcome : ToolOutcome.Partial, gone ? null : "Some items are still in place."));
    }
}
