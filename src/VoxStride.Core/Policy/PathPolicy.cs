namespace VoxStride.Core.Policy;

public enum PathDecision { Allowed, OutsideRoots, Traversal, ReparsePoint, Hidden, System, Network, Invalid, Protected }

/// <summary>
/// Canonicalizes and checks paths against explicitly allowed roots. Rejects traversal, reparse points
/// (junctions/symlinks) anywhere between the root and the target, UNC/network paths unless allowed,
/// hidden/system items by default and well-known protected locations.
/// </summary>
public sealed class PathPolicy
{
    private readonly List<string> _roots = [];
    private static readonly StringComparison Cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public bool AllowNetwork { get; init; }
    public bool AllowHidden { get; init; }

    public IReadOnlyList<string> Roots => _roots;

    public PathPolicy(IEnumerable<string> allowedRoots)
    {
        foreach (var r in allowedRoots)
        {
            var full = Normalize(Path.GetFullPath(r));
            if (!_roots.Contains(full, StringComparer.FromComparison(Cmp))) _roots.Add(full);
        }
    }

    public static string Normalize(string full) =>
        full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;

    public PathDecision Check(string candidate, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) || candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return PathDecision.Invalid;
        if (candidate.Contains("..", StringComparison.Ordinal) &&
            candidate.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..")) return PathDecision.Traversal;
        if (!AllowNetwork && (candidate.StartsWith(@"\\", StringComparison.Ordinal) || candidate.StartsWith("//", StringComparison.Ordinal)))
            return PathDecision.Network;
        string full;
        try { full = Normalize(Path.GetFullPath(candidate)); }
        catch (Exception) { return PathDecision.Invalid; }
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) return PathDecision.Invalid;

        var root = _roots.FirstOrDefault(r => IsUnder(full, r));
        if (root is null) return PathDecision.OutsideRoots;
        if (IsProtectedLocation(full)) return PathDecision.Protected;

        // Walk every existing component from root to target and reject reparse points.
        var rel = Path.GetRelativePath(root, full);
        var current = root;
        if (IsReparse(current)) return PathDecision.ReparsePoint;
        if (rel != ".")
        {
            foreach (var part in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (!Path.Exists(current)) break;
                if (IsReparse(current)) return PathDecision.ReparsePoint;
                if (!AllowHidden)
                {
                    var attrs = File.GetAttributes(current);
                    if ((attrs & FileAttributes.System) != 0) return PathDecision.System;
                    if ((attrs & FileAttributes.Hidden) != 0 || part.StartsWith('.')) return PathDecision.Hidden;
                }
            }
        }
        canonical = full;
        return PathDecision.Allowed;
    }

    public static bool IsUnder(string full, string root)
    {
        if (string.Equals(full, root, Cmp)) return true;
        var withSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(withSep, Cmp);
    }

    public static bool IsReparse(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
            var dir = new DirectoryInfo(path);
            if (dir.Exists) return dir.LinkTarget is not null || (dir.Attributes & FileAttributes.ReparsePoint) != 0;
            return false;
        }
        catch (Exception) { return true; } // fail closed
    }

    private static readonly string[] ProtectedFragments =
    [
        @"\Windows\", @"\Program Files", @"\ProgramData\", @"\AppData\Roaming\Microsoft\Credentials", @"\AppData\Local\Microsoft\Credentials",
        @"\AppData\Roaming\Microsoft\Protect", @"\.ssh", @"\.gnupg", @"\$Recycle.Bin",
    ];

    public static bool IsProtectedLocation(string full)
    {
        var p = full.Replace('/', '\\') + "\\";
        return ProtectedFragments.Any(f => p.Contains(f, StringComparison.OrdinalIgnoreCase));
    }
}
