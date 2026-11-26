using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Policy;

namespace VoxStride.Core.Files;

/// <summary>Resolves Windows known folders (redirected/OneDrive aware on Windows).</summary>
public interface IKnownFolders
{
    string? Resolve(KnownFolder folder);
}

public enum KnownFolder { Desktop, Documents, Downloads, Pictures, Music, Videos, Temp }

public sealed record OrganizeItem(string SourcePath, string DestinationPath, string Category, long Length, DateTime LastWriteUtc);
public sealed record SkippedItem(string Path, string Reason);

public sealed record OrganizationManifest(
    string ManifestId,
    string Root,
    IReadOnlyList<OrganizeItem> Items,
    IReadOnlyList<SkippedItem> Skipped,
    IReadOnlyList<string> FoldersToCreate,
    string Digest,
    DateTimeOffset CreatedAt)
{
    public JsonObject Summary(int maxItems = 40)
    {
        var byCat = new JsonObject();
        foreach (var g in Items.GroupBy(i => i.Category).OrderBy(g => g.Key, StringComparer.Ordinal)) byCat[g.Key] = g.Count();
        var skipReasons = new JsonObject();
        foreach (var g in Skipped.GroupBy(s => s.Reason).OrderBy(g => g.Key, StringComparer.Ordinal)) skipReasons[g.Key] = g.Count();
        var sample = new JsonArray();
        foreach (var i in Items.Take(maxItems))
            sample.Add(new JsonObject { ["file"] = Path.GetFileName(i.SourcePath), ["to"] = Path.GetRelativePath(Root, i.DestinationPath) });
        return new JsonObject
        {
            ["manifestId"] = ManifestId,
            ["moveCount"] = Items.Count,
            ["byCategory"] = byCat,
            ["skippedCount"] = Skipped.Count,
            ["skippedReasons"] = skipReasons,
            ["newFolders"] = new JsonArray(FoldersToCreate.Select(f => (JsonNode)Path.GetFileName(f)!).ToArray()),
            ["sample"] = sample,
        };
    }
}

public sealed class OrganizeOptions
{
    /// <summary>If non-empty, only these categories are moved.</summary>
    public HashSet<string> IncludeCategories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExcludeCategories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Extensions (with dot) that must not be moved, e.g. ".zip".</summary>
    public HashSet<string> ExcludeExtensions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool MoveUnknownToOther { get; init; }
    public int MaxFiles { get; init; } = 5000;
}

/// <summary>Deterministic extension → category rules (no content upload).</summary>
public static class FileCategories
{
    public static readonly IReadOnlyDictionary<string, string> Map = Build();

    public static readonly string[] All = ["Documents", "Spreadsheets", "Presentations", "PDFs", "Images", "Audio", "Videos", "Archives", "Installers", "Code", "Other"];

    private static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string cat, params string[] exts) { foreach (var e in exts) d["." + e] = cat; }
        Add("Documents", "doc", "docx", "odt", "rtf", "txt", "md", "pages", "wpd", "tex");
        Add("Spreadsheets", "xls", "xlsx", "xlsm", "ods", "csv", "tsv", "numbers");
        Add("Presentations", "ppt", "pptx", "odp", "key");
        Add("PDFs", "pdf");
        Add("Images", "jpg", "jpeg", "png", "gif", "bmp", "webp", "heic", "heif", "tif", "tiff", "svg", "ico", "raw", "cr2", "nef", "psd", "avif");
        Add("Audio", "mp3", "wav", "flac", "aac", "m4a", "ogg", "wma", "opus", "aiff");
        Add("Videos", "mp4", "mkv", "mov", "avi", "wmv", "webm", "m4v", "flv", "3gp");
        Add("Archives", "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "tgz", "iso", "cab");
        Add("Installers", "exe", "msi", "msix", "appx", "msixbundle", "appxbundle");
        Add("Code", "cs", "py", "js", "ts", "java", "cpp", "c", "h", "go", "rs", "rb", "php", "html", "css", "json", "xml", "yml", "yaml", "sql", "ps1", "sh", "bat", "ipynb");
        return d;
    }

    public static string? Categorize(string fileName) =>
        Map.TryGetValue(Path.GetExtension(fileName), out var c) ? c : null;

    /// <summary>Normalizes a spoken/typed category ("pictures", "photos", "zip files") to a canonical category.</summary>
    public static string? Normalize(string spoken)
    {
        var s = spoken.Trim().ToLowerInvariant().TrimEnd('s');
        return s switch
        {
            "document" or "doc" or "text file" or "word document" or "word file" => "Documents",
            "spreadsheet" or "excel file" or "excel" or "sheet" or "csv" => "Spreadsheets",
            "presentation" or "powerpoint" or "slide" or "deck" => "Presentations",
            "pdf" or "pdf file" => "PDFs",
            "image" or "picture" or "photo" or "screenshot" or "pic" => "Images",
            "audio" or "music" or "song" or "audio file" or "mp3" => "Audio",
            "video" or "movie" or "clip" or "mp4" => "Videos",
            "archive" or "zip" or "zip file" or "compressed file" or "rar" => "Archives",
            "installer" or "setup file" or "exe" or "program" => "Installers",
            "code" or "code file" or "script" or "source file" => "Code",
            "other" => "Other",
            _ => FileCategories.All.FirstOrDefault(c => c.Equals(spoken.Trim(), StringComparison.OrdinalIgnoreCase)),
        };
    }
}

/// <summary>Builds an exact, verifiable organization manifest. Preview has no side effects.</summary>
public sealed class OrganizationPlanner(TimeProvider time)
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public OrganizationManifest Preview(string root, OrganizeOptions options, PathPolicy policy)
    {
        if (policy.Check(root, out var canonicalRoot) != PathDecision.Allowed) throw new UnauthorizedAccessException("Root is not an allowed location.");
        var items = new List<OrganizeItem>();
        var skipped = new List<SkippedItem>();
        var plannedDest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newFolders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        var dir = new DirectoryInfo(canonicalRoot);
        var count = 0;
        foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 })
                     .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (entry is DirectoryInfo) continue; // existing folders are never touched
            if (++count > options.MaxFiles) { skipped.Add(new SkippedItem(entry.FullName, "limit-reached")); continue; }
            var f = (FileInfo)entry;
            var reason = SkipReason(f, options, out var category);
            if (reason is not null) { skipped.Add(new SkippedItem(f.FullName, reason)); continue; }

            var destDir = Path.Combine(canonicalRoot, category!);
            if (File.Exists(destDir)) { skipped.Add(new SkippedItem(f.FullName, "category-name-is-a-file")); continue; }
            if (Directory.Exists(destDir) && PathPolicy.IsReparse(destDir)) { skipped.Add(new SkippedItem(f.FullName, "destination-is-link")); continue; }
            if (!Directory.Exists(destDir)) newFolders.Add(destDir);

            var dest = UniqueDestination(destDir, f.Name, plannedDest);
            if (dest is null) { skipped.Add(new SkippedItem(f.FullName, "name-collision")); continue; }
            if (policy.Check(dest, out _) is not (PathDecision.Allowed)) { skipped.Add(new SkippedItem(f.FullName, "destination-not-allowed")); continue; }
            plannedDest.Add(dest);
            items.Add(new OrganizeItem(f.FullName, dest, category!, f.Length, f.LastWriteTimeUtc));
        }

        var id = "m-" + Guid.NewGuid().ToString("N")[..12];
        var digest = ComputeDigest(canonicalRoot, items);
        return new OrganizationManifest(id, canonicalRoot, items, skipped, newFolders.ToList(), digest, time.GetUtcNow());
    }

    private static string? SkipReason(FileInfo f, OrganizeOptions o, out string? category)
    {
        category = null;
        var a = f.Attributes;
        if ((a & FileAttributes.ReparsePoint) != 0 || f.LinkTarget is not null) return "link";
        if ((a & FileAttributes.System) != 0) return "system";
        if ((a & FileAttributes.Hidden) != 0 || f.Name.StartsWith('.')) return "hidden";
        if ((a & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0) return "cloud-placeholder";
        var ext = f.Extension;
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) return "shortcut";
        if (f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || f.Name.StartsWith("~$", StringComparison.Ordinal)) return "system";
        if (ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || ext.Equals(".crdownload", StringComparison.OrdinalIgnoreCase) || ext.Equals(".part", StringComparison.OrdinalIgnoreCase)) return "in-progress-or-temp";
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(f.Name))) return "reserved-name";
        if (o.ExcludeExtensions.Contains(ext)) return "excluded-by-you";
        category = FileCategories.Categorize(f.Name);
        if (category is null)
        {
            if (!o.MoveUnknownToOther) return "unknown-type";
            category = "Other";
        }
        if (o.ExcludeCategories.Contains(category)) return "excluded-by-you";
        if (o.IncludeCategories.Count > 0 && !o.IncludeCategories.Contains(category)) return "not-requested";
        return null;
    }

    private static string? UniqueDestination(string destDir, string name, HashSet<string> planned)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 1; n < 1000; n++)
        {
            var candidate = Path.Combine(destDir, n == 1 ? name : $"{baseName} ({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && !planned.Contains(candidate)) return candidate;
        }
        return null;
    }

    public static string ComputeDigest(string root, IEnumerable<OrganizeItem> items)
    {
        var sb = new StringBuilder(root.ToUpperInvariant()).Append('\n');
        foreach (var i in items)
            sb.Append(i.SourcePath).Append('|').Append(i.DestinationPath).Append('|').Append(i.Length).Append('|').Append(i.LastWriteUtc.Ticks).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }
}

/// <summary>Session-scoped store of previewed manifests. Manifests expire and are single-use.</summary>
public sealed class ManifestStore(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, OrganizationManifest> _org = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CleanupManifest> _cleanup = new(StringComparer.Ordinal);
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(15);

    public void Put(OrganizationManifest m) => _org[m.ManifestId] = m;
    public void Put(CleanupManifest m) => _cleanup[m.ManifestId] = m;

    public OrganizationManifest? GetOrganization(string id) =>
        _org.TryGetValue(id, out var m) && time.GetUtcNow() - m.CreatedAt < Lifetime ? m : null;
    public CleanupManifest? GetCleanup(string id) =>
        _cleanup.TryGetValue(id, out var m) && time.GetUtcNow() - m.CreatedAt < Lifetime ? m : null;
    public bool Consume(string id) => _org.TryRemove(id, out _) | _cleanup.TryRemove(id, out _);
}
