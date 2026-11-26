using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VoxStride.Core.Execution;
using VoxStride.Core.Policy;

namespace VoxStride.Core.Files;

public sealed record CleanupItem(string Path, long Length, DateTime LastWriteUtc);

public sealed record CleanupManifest(
    string ManifestId,
    IReadOnlyList<string> Roots,
    IReadOnlyList<CleanupItem> Items,
    IReadOnlyList<SkippedItem> Skipped,
    long EligibleBytes,
    string Digest,
    DateTimeOffset CreatedAt,
    bool Truncated);

public interface IFileRemover
{
    /// <summary>Removes one file. Recycle sends it to the Recycle Bin where supported.</summary>
    void Remove(string path, bool recycle);
    bool SupportsRecycle { get; }
}

public sealed class PermanentFileRemover : IFileRemover
{
    public bool SupportsRecycle => false;
    public void Remove(string path, bool recycle)
    {
        if (recycle) throw new NotSupportedException("Recycle Bin is not available on this platform.");
        File.Delete(path);
    }
}

public sealed record CleanupResult(int Removed, long RemovedBytes, int Skipped, IReadOnlyList<SkippedItem> Details);

/// <summary>Safe temp cleanup: only configured user temp roots, bounded scan, never forces, measures real disk change.</summary>
public sealed class TempCleaner(TimeProvider time)
{
    public TimeSpan MinAge { get; init; } = TimeSpan.FromHours(24);
    public int MaxFiles { get; init; } = 20000;
    public TimeSpan MaxScan { get; init; } = TimeSpan.FromSeconds(8);

    public CleanupManifest Preview(IReadOnlyList<string> roots, PathPolicy policy)
    {
        var items = new List<CleanupItem>();
        var skipped = new List<SkippedItem>();
        var sw = Stopwatch.StartNew();
        var truncated = false;
        var cutoff = time.GetUtcNow().UtcDateTime - MinAge;
        var canonicalRoots = new List<string>();

        foreach (var root in roots)
        {
            if (policy.Check(root, out var canonicalRoot) != PathDecision.Allowed || !Directory.Exists(canonicalRoot)) continue;
            canonicalRoots.Add(canonicalRoot);
            var stack = new Stack<string>();
            stack.Push(canonicalRoot);
            while (stack.Count > 0)
            {
                if (items.Count >= MaxFiles || sw.Elapsed > MaxScan) { truncated = true; break; }
                var dir = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
                catch (Exception) { skipped.Add(new SkippedItem(dir, "inaccessible")); continue; }
                foreach (var e in entries)
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0 || e.LinkTarget is not null) { skipped.Add(new SkippedItem(e.FullName, "link")); continue; }
                    if ((e.Attributes & FileAttributes.System) != 0) { skipped.Add(new SkippedItem(e.FullName, "system")); continue; }
                    if (e is DirectoryInfo d) { stack.Push(d.FullName); continue; }
                    var f = (FileInfo)e;
                    if (f.LastWriteTimeUtc > cutoff || f.LastAccessTimeUtc > cutoff) { skipped.Add(new SkippedItem(f.FullName, "recent")); continue; }
                    items.Add(new CleanupItem(f.FullName, f.Length, f.LastWriteTimeUtc));
                }
            }
        }
        var digestSrc = new StringBuilder();
        foreach (var i in items) digestSrc.Append(i.Path).Append('|').Append(i.Length).Append('|').Append(i.LastWriteUtc.Ticks).Append('\n');
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestSrc.ToString())))[..32];
        return new CleanupManifest("c-" + Guid.NewGuid().ToString("N")[..12], canonicalRoots, items, skipped, items.Sum(i => i.Length), digest, time.GetUtcNow(), truncated);
    }

    public CleanupResult Apply(CleanupManifest manifest, PathPolicy policy, IFileRemover remover, bool recycle, ToolContext ctx, IProgress<(int, int)>? progress, CancellationToken ct)
    {
        int removed = 0, skippedCount = 0;
        long bytes = 0;
        var details = new List<SkippedItem>();
        var i = 0;
        foreach (var item in manifest.Items)
        {
            ctx.EnsureCanWrite(ct);
            i++;
            if (policy.Check(item.Path, out _) != PathDecision.Allowed) { skippedCount++; details.Add(new SkippedItem(item.Path, "no-longer-allowed")); continue; }
            var f = new FileInfo(item.Path);
            if (!f.Exists) { skippedCount++; details.Add(new SkippedItem(item.Path, "already-gone")); continue; }
            if (f.Length != item.Length || f.LastWriteTimeUtc != item.LastWriteUtc || (f.Attributes & FileAttributes.ReparsePoint) != 0)
            { skippedCount++; details.Add(new SkippedItem(item.Path, "changed-since-preview")); continue; }
            try
            {
                remover.Remove(item.Path, recycle);
                if (!File.Exists(item.Path)) { removed++; bytes += item.Length; }
                else { skippedCount++; details.Add(new SkippedItem(item.Path, "still-present")); }
            }
            catch (IOException) { skippedCount++; details.Add(new SkippedItem(item.Path, "in-use")); }
            catch (UnauthorizedAccessException) { skippedCount++; details.Add(new SkippedItem(item.Path, "access-denied")); }
            if (i % 50 == 0) progress?.Report((i, manifest.Items.Count));
        }
        progress?.Report((manifest.Items.Count, manifest.Items.Count));
        return new CleanupResult(removed, bytes, skippedCount, details);
    }

    public static JsonObject Summary(CleanupManifest m)
    {
        var reasons = new JsonObject();
        foreach (var g in m.Skipped.GroupBy(s => s.Reason).OrderBy(g => g.Key, StringComparer.Ordinal)) reasons[g.Key] = g.Count();
        return new JsonObject
        {
            ["manifestId"] = m.ManifestId,
            ["eligibleFiles"] = m.Items.Count,
            ["eligibleBytes"] = m.EligibleBytes,
            ["eligibleReadable"] = Bytes.Format(m.EligibleBytes),
            ["skippedReasons"] = reasons,
            ["scanTruncated"] = m.Truncated,
            ["note"] = "Estimated bytes; actual reclaimed space is measured after removal.",
        };
    }
}

public static class Bytes
{
    public static string Format(long bytes)
    {
        string[] u = ["bytes", "KB", "MB", "GB", "TB"];
        double v = bytes; var i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{bytes:N0} bytes" : $"{v:0.##} {u[i]}";
    }

    public static string FormatBinary(long bytes)
    {
        string[] u = ["bytes", "KiB", "MiB", "GiB", "TiB"];
        double v = bytes; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes:N0} bytes" : $"{v:0.##} {u[i]}";
    }
}
