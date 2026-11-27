using System.IO;
using Microsoft.Win32;

namespace VoxStride.Windows;

/// <summary>
/// Detects whether another app (Teams, Zoom, Discord, a browser call…) is currently using the microphone, using the
/// same per-app usage records Windows shows in Settings › Privacy › Microphone. Read-only: nothing is changed.
/// </summary>
public static class MicrophoneUsage
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>Returns the name of another app using the microphone right now, or null.</summary>
    public static string? OtherAppInUse(string ownExeName = "VoxStride.exe")
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            if (root is null) return null;
            foreach (var name in root.GetSubKeyNames())
            {
                if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using var np = root.OpenSubKey(name);
                    if (np is null) continue;
                    foreach (var app in np.GetSubKeyNames())
                    {
                        if (app.EndsWith(ownExeName, StringComparison.OrdinalIgnoreCase)) continue;
                        using var k = np.OpenSubKey(app);
                        if (InUse(k)) return Friendly(app.Split('#').LastOrDefault() ?? app);
                    }
                }
                else
                {
                    using var k = root.OpenSubKey(name);
                    if (InUse(k)) return Friendly(name.Split('_').FirstOrDefault() ?? name);
                }
            }
        }
        catch (Exception) { }
        return null;
    }

    private static bool InUse(RegistryKey? k)
    {
        if (k is null) return false;
        var start = k.GetValue("LastUsedTimeStart") is long s ? s : 0;
        var stop = k.GetValue("LastUsedTimeStop") is long e ? e : -1;
        return start > 0 && stop == 0;
    }

    private static string Friendly(string raw)
    {
        var n = Path.GetFileNameWithoutExtension(raw);
        return n.Contains("Teams", StringComparison.OrdinalIgnoreCase) ? "Microsoft Teams" : n;
    }
}
