using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VoxStride.Core.Updates;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, Uri InstallerUrl, string InstallerName, long Size, Uri? ChecksumsUrl, DateTimeOffset PublishedAt);

/// <summary>Pure logic for the GitHub Releases updater (parsing, version comparison, checksum verification).</summary>
public static partial class UpdateLogic
{
    public const string ReleasesApi = "https://api.github.com/repos/nisar87/VoxStride/releases/latest";

    public static Version? ParseTag(string tag)
    {
        var m = TagRegex().Match(tag.Trim());
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? Normalize(v) : null;
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), 0);

    /// <summary>Returns the update if the latest release is newer than <paramref name="current"/> and has a Windows installer.</summary>
    public static UpdateInfo? Evaluate(JsonNode release, Version current)
    {
        if (release["draft"]?.GetValue<bool>() == true || release["prerelease"]?.GetValue<bool>() == true) return null;
        var tag = release["tag_name"]?.GetValue<string>() ?? "";
        var v = ParseTag(tag);
        if (v is null || v <= Normalize(current)) return null;
        if (release["assets"] is not JsonArray assets) return null;
        JsonNode? installer = null, sums = null;
        foreach (var a in assets)
        {
            var name = a?["name"]?.GetValue<string>() ?? "";
            if (InstallerRegex().IsMatch(name)) installer = a;
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = a;
        }
        if (installer is null) return null;
        var url = new Uri(installer["browser_download_url"]!.GetValue<string>());
        if (url.Scheme != Uri.UriSchemeHttps || !url.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        return new UpdateInfo(v, tag, release["body"]?.GetValue<string>() ?? "", url, installer["name"]!.GetValue<string>(),
            installer["size"]?.GetValue<long>() ?? 0, sums?["browser_download_url"]?.GetValue<string>() is { } s ? new Uri(s) : null,
            DateTimeOffset.TryParse(release["published_at"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var p) ? p : DateTimeOffset.MinValue);
    }

    /// <summary>Finds the expected SHA-256 for a file in a "hash  filename" checksum list.</summary>
    public static string? ExpectedHash(string checksums, string fileName)
    {
        foreach (var line in checksums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = SumLine().Match(line);
            if (m.Success && m.Groups[2].Value.Trim().TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return m.Groups[1].Value.ToUpperInvariant();
        }
        return null;
    }

    [GeneratedRegex(@"^v?(\d+\.\d+(?:\.\d+)?)$")]
    private static partial Regex TagRegex();
    [GeneratedRegex(@"^VoxStride-Setup-[\d.]+-x64\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerRegex();
    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+(.+)$")]
    private static partial Regex SumLine();
}
