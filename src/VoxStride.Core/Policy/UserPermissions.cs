namespace VoxStride.Core.Policy;

public enum ScreenCapturePolicy { AskEachTime, Never }

/// <summary>
/// The user's own permission choices (Settings › Permissions), consulted by tools on top of VoxStride's fixed safety
/// rules (which can't be loosened here): apps VoxStride must not open or control, websites it must not visit, and
/// whether window screenshots may be taken for the vision fallback. Folder access lives in <see cref="Tools.FileScope"/>.
/// </summary>
public sealed class UserPermissions(
    Func<IReadOnlyCollection<string>>? blockedApps = null,
    Func<IReadOnlyCollection<string>>? blockedSites = null,
    Func<ScreenCapturePolicy>? screenCapture = null)
{
    public static readonly UserPermissions Default = new();

    public ScreenCapturePolicy ScreenCapture => screenCapture?.Invoke() ?? ScreenCapturePolicy.AskEachTime;

    /// <summary>Blocked if the user listed the app's name, its exe file name, or a word of its display name.</summary>
    public bool AppAllowed(string displayName, string? targetPath, out string reason)
    {
        reason = "";
        var file = targetPath is null ? "" : targetPath[(targetPath.LastIndexOfAny(['\\', '/']) + 1)..]; // Windows paths, on any OS
        var exe = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : Path.GetFileNameWithoutExtension(file);
        foreach (var raw in blockedApps?.Invoke() ?? [])
        {
            var b = raw.Trim();
            if (b.Length == 0) continue;
            var bStem = b.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? b[..^4] : b;
            if (displayName.Equals(b, StringComparison.OrdinalIgnoreCase) || exe.Equals(bStem, StringComparison.OrdinalIgnoreCase)
                || displayName.Contains(b, StringComparison.OrdinalIgnoreCase) && b.Length >= 4)
            {
                reason = $"You've blocked {displayName} in Settings › Permissions.";
                return false;
            }
        }
        return true;
    }

    /// <summary>Blocked if the host equals or is a subdomain of a listed site (e.g. "facebook.com" blocks "m.facebook.com").</summary>
    public bool SiteAllowed(Uri uri, out string reason)
    {
        reason = "";
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        foreach (var raw in blockedSites?.Invoke() ?? [])
        {
            var site = Normalize(raw);
            if (site.Length == 0) continue;
            if (host == site || host.EndsWith("." + site, StringComparison.Ordinal))
            {
                reason = $"You've blocked {site} in Settings › Permissions.";
                return false;
            }
        }
        return true;
    }

    /// <summary>"https://www.Example.com/path" → "example.com".</summary>
    public static string Normalize(string site)
    {
        var s = site.Trim().ToLowerInvariant();
        if (Uri.TryCreate(s.Contains("://", StringComparison.Ordinal) ? s : "https://" + s, UriKind.Absolute, out var u)) s = u.Host;
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
        return s.TrimEnd('.');
    }
}
