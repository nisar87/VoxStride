using VoxStride.Contracts;
using VoxStride.Core.Execution;
using VoxStride.Core.Policy;

namespace VoxStride.Core.Files;

public sealed record MoveBatchResult(int Moved, int Skipped, int Failed, int Unknown, IReadOnlyList<SkippedItem> Details, IReadOnlyList<string> MovedDestinations);

/// <summary>Executes approved manifests with per-item revalidation, journaling, verification and undo.</summary>
public sealed class MoveExecutor(IExecutionJournal journal, TimeProvider time)
{
    public async Task<MoveBatchResult> ApplyAsync(OrganizationManifest manifest, PathPolicy policy, ToolContext ctx, IProgress<(int done, int total)>? progress, CancellationToken ct)
    {
        int moved = 0, skipped = 0, failed = 0, unknown = 0;
        var details = new List<SkippedItem>();
        var movedDest = new List<string>();
        var total = manifest.Items.Count;
        var createdHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in manifest.Items)
        {
            ctx.EnsureCanWrite(ct); // stop dispatching new moves immediately after cancel

            // Revalidate as close to the effect as possible (TOCTOU).
            if (policy.Check(item.SourcePath, out _) != PathDecision.Allowed || policy.Check(item.DestinationPath, out _) != PathDecision.Allowed)
            { skipped++; details.Add(new SkippedItem(item.SourcePath, "no-longer-allowed")); continue; }
            var src = new FileInfo(item.SourcePath);
            if (!src.Exists || src.Length != item.Length || src.LastWriteTimeUtc != item.LastWriteUtc || (src.Attributes & FileAttributes.ReparsePoint) != 0)
            { skipped++; details.Add(new SkippedItem(item.SourcePath, "changed-since-preview")); continue; }
            if (File.Exists(item.DestinationPath) || Directory.Exists(item.DestinationPath))
            { skipped++; details.Add(new SkippedItem(item.SourcePath, "destination-now-exists")); continue; }

            var destDir = Path.GetDirectoryName(item.DestinationPath)!;
            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
                if (createdHere.Add(destDir))
                    await journal.RecordCreatedFolderAsync(new CreatedFolderRecord(ctx.Auth.RunId, manifest.ManifestId, destDir), CancellationToken.None);
            }
            else if (PathPolicy.IsReparse(destDir))
            { skipped++; details.Add(new SkippedItem(item.SourcePath, "destination-is-link")); continue; }

            var rec = new MoveRecord(Guid.NewGuid(), ctx.Auth.RunId, manifest.ManifestId, item.SourcePath, item.DestinationPath, item.Length, item.LastWriteUtc, "intent", time.GetUtcNow());
            await journal.RecordMoveAsync(rec, CancellationToken.None);
            try
            {
                File.Move(item.SourcePath, item.DestinationPath, overwrite: false);
            }
            catch (IOException ex) when (File.Exists(item.SourcePath) && !File.Exists(item.DestinationPath))
            {
                failed++;
                details.Add(new SkippedItem(item.SourcePath, IsLocked(ex) ? "in-use" : "move-failed"));
                await journal.RecordMoveAsync(rec with { Status = "failed", At = time.GetUtcNow() }, CancellationToken.None);
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                failed++;
                details.Add(new SkippedItem(item.SourcePath, "access-denied"));
                await journal.RecordMoveAsync(rec with { Status = File.Exists(item.SourcePath) ? "failed" : "unknown", At = time.GetUtcNow() }, CancellationToken.None);
                continue;
            }
            catch (Exception)
            {
                unknown++;
                details.Add(new SkippedItem(item.SourcePath, "unknown-outcome"));
                await journal.RecordMoveAsync(rec with { Status = "unknown", At = time.GetUtcNow() }, CancellationToken.None);
                continue;
            }

            var d = new FileInfo(item.DestinationPath);
            if (d.Exists && d.Length == item.Length && !File.Exists(item.SourcePath))
            {
                moved++;
                movedDest.Add(item.DestinationPath);
                await journal.RecordMoveAsync(rec with { Status = "moved", At = time.GetUtcNow() }, CancellationToken.None);
            }
            else
            {
                unknown++;
                details.Add(new SkippedItem(item.SourcePath, "unverified"));
                await journal.RecordMoveAsync(rec with { Status = "unknown", At = time.GetUtcNow() }, CancellationToken.None);
            }
            progress?.Report((moved + skipped + failed + unknown, total));
        }
        return new MoveBatchResult(moved, skipped, failed, unknown, details, movedDest);
    }

    /// <summary>Independent verification from the journal: each "moved" record must exist at destination and not at source.</summary>
    public async Task<(int verified, int mismatched)> VerifyAsync(string manifestId, CancellationToken ct)
    {
        int ok = 0, bad = 0;
        foreach (var m in await journal.GetMovesAsync(manifestId, ct))
        {
            if (m.Status != "moved") continue;
            if (File.Exists(m.DestinationPath) && !File.Exists(m.SourcePath) && new FileInfo(m.DestinationPath).Length == m.Length) ok++; else bad++;
        }
        return (ok, bad);
    }

    public sealed record UndoPlanItem(MoveRecord Move, string? Conflict);

    public async Task<IReadOnlyList<UndoPlanItem>> PlanUndoAsync(string manifestId, CancellationToken ct)
    {
        var list = new List<UndoPlanItem>();
        foreach (var m in (await journal.GetMovesAsync(manifestId, ct)).Where(m => m.Status == "moved").Reverse())
        {
            string? conflict = null;
            var d = new FileInfo(m.DestinationPath);
            if (!d.Exists) conflict = "moved-file-missing";
            else if (d.Length != m.Length || d.LastWriteTimeUtc != m.LastWriteUtc) conflict = "edited-after-move";
            else if (File.Exists(m.SourcePath) || Directory.Exists(m.SourcePath)) conflict = "original-location-occupied";
            list.Add(new UndoPlanItem(m, conflict));
        }
        return list;
    }

    public async Task<MoveBatchResult> UndoAsync(string manifestId, PathPolicy policy, ToolContext ctx, CancellationToken ct)
    {
        int restored = 0, skipped = 0, failed = 0;
        var details = new List<SkippedItem>();
        var restoredList = new List<string>();
        foreach (var p in await PlanUndoAsync(manifestId, ct))
        {
            ctx.EnsureCanWrite(ct);
            var m = p.Move;
            var conflict = p.Conflict;
            if (conflict is null && (policy.Check(m.SourcePath, out _) != PathDecision.Allowed || policy.Check(m.DestinationPath, out _) != PathDecision.Allowed)) conflict = "no-longer-allowed";
            // Re-check immediately before the effect.
            if (conflict is null && (File.Exists(m.SourcePath) || !File.Exists(m.DestinationPath))) conflict = "changed";
            if (conflict is not null)
            {
                skipped++; details.Add(new SkippedItem(m.DestinationPath, conflict));
                await journal.RecordMoveAsync(m with { Status = "undo-conflict", At = time.GetUtcNow() }, CancellationToken.None);
                continue;
            }
            try
            {
                File.Move(m.DestinationPath, m.SourcePath, overwrite: false);
                if (File.Exists(m.SourcePath) && !File.Exists(m.DestinationPath))
                {
                    restored++; restoredList.Add(m.SourcePath);
                    await journal.RecordMoveAsync(m with { Status = "undone", At = time.GetUtcNow() }, CancellationToken.None);
                }
                else { failed++; details.Add(new SkippedItem(m.DestinationPath, "unverified")); }
            }
            catch (Exception)
            {
                failed++; details.Add(new SkippedItem(m.DestinationPath, "undo-failed"));
            }
        }
        // Remove only folders created by this run that are still empty.
        foreach (var f in await journal.GetCreatedFoldersAsync(manifestId, ct))
        {
            try
            {
                if (Directory.Exists(f.Path) && !PathPolicy.IsReparse(f.Path) && !Directory.EnumerateFileSystemEntries(f.Path).Any())
                    Directory.Delete(f.Path, recursive: false);
            }
            catch (IOException) { /* folder in use or no longer empty: leave it */ }
        }
        return new MoveBatchResult(restored, skipped, failed, 0, details, restoredList);
    }

    private static bool IsLocked(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33; // sharing / lock violation
}
