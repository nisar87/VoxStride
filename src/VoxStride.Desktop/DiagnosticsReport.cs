using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using VoxStride.Contracts;
using VoxStride.Speech;

namespace VoxStride.Desktop;

/// <summary>
/// Describes the machine and VoxStride's configuration for troubleshooting, and exports a zip with the logs.
/// Contains no API keys (only whether each key is present), no audio and no history.
/// </summary>
public static class DiagnosticsReport
{
    public static string Environment(AppSettings settings, AppHost? host)
    {
        var sb = new StringBuilder();
        void Line(string k, object? v) => sb.Append(k.PadRight(22)).Append(": ").AppendLine(Convert.ToString(v, CultureInfo.InvariantCulture));
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        Line("VoxStride", UpdateService.CurrentVersion.ToString(3) + "  log session " + DiagnosticsLog.SessionId);
        Line("Executable", System.Environment.ProcessPath);
        Line("Windows", WindowsVersion());
        Line("Runtime", RuntimeInformation.FrameworkDescription + " " + RuntimeInformation.ProcessArchitecture);
        Line("CPU cores / memory", $"{System.Environment.ProcessorCount} / {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1_048_576} MB available");
        Line("Process", $"pid {proc.Id}, {proc.WorkingSet64 / 1_048_576} MB, {proc.Threads.Count} threads, {proc.HandleCount} handles, up {(DateTime.Now - proc.StartTime).TotalMinutes:0} min");
        Line("Culture / UI / zone", $"{CultureInfo.CurrentCulture.Name} / {CultureInfo.CurrentUICulture.Name} / {TimeZoneInfo.Local.Id}");
        Line("Uptime (system)", TimeSpan.FromMilliseconds(System.Environment.TickCount64).ToString(@"d\.hh\:mm", CultureInfo.InvariantCulture));
        sb.AppendLine();
        Line("Speech engine", host?.SpeechEngineLabel ?? "(not started)");
        Line("Microphone (chosen)", settings.MicrophoneName ?? "Windows default");
        Line("Microphone (in use)", host?.MicrophoneLabel ?? "-");
        var devices = Safe(() => WaveInRecorder.Devices().ToArray(), []);
        var def = Safe(WaveInRecorder.DefaultDeviceIndex, -1);
        for (var i = 0; i < devices.Length; i++) Line($"  input device {i}", devices[i] + (i == def ? "   <- Windows default" : ""));
        if (devices.Length == 0) Line("  input devices", "none found");
        Line("Speech recognizers", string.Join(", ", Safe(() => SpeechEnvironment.RecognizerCultures().ToArray(), [])) is { Length: > 0 } r ? r : "none installed");
        Line("Windows voices", string.Join(", ", Safe(() => SpeechEnvironment.Voices().ToArray(), [])));
        sb.AppendLine();
        if (host is not null)
            foreach (var k in Enum.GetValues<ProviderKind>())
                Line($"{k} key", host.Secrets.Load(ProviderSelector.SecretName(k)) is { Length: > 0 } ? "present" : "not set");
        sb.AppendLine();
        sb.AppendLine("Settings:");
        foreach (var (k, v) in settings.Snapshot().OrderBy(p => p.Key, StringComparer.Ordinal)) sb.Append("  ").Append(k.PadRight(28)).Append(' ').AppendLine(v);
        return sb.ToString();
    }

    /// <summary>Writes a zip with all log files and the environment summary; returns the path.</summary>
    public static string Export(string zipPath, AppSettings settings, AppHost? host, params (string Name, string Content)[] extra)
    {
        DiagnosticsLog.Flush(TimeSpan.FromSeconds(2));
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var env = zip.CreateEntry("environment.txt");
        using (var w = new StreamWriter(env.Open(), new UTF8Encoding(false))) w.Write(Environment(settings, host));
        foreach (var (name, content) in extra)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(Log.Redact(content));
        }
        foreach (var f in DiagnosticsLog.Files())
        {
            // The live log is open for writing; copy through a shared read.
            var entry = zip.CreateEntry(Path.GetFileName(f));
            using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = entry.Open();
            src.CopyTo(dst);
        }
        Log.Info("app", $"diagnostics report saved ({new FileInfo(zipPath).Length / 1024} KB)");
        return zipPath;
    }

    /// <summary>"Windows 11 24H2 (build 26100.2605)" from the registry (read-only).</summary>
    public static string WindowsVersion()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = k?.GetValue("CurrentBuildNumber") as string ?? System.Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
            var ubr = k?.GetValue("UBR") is int u ? "." + u.ToString(CultureInfo.InvariantCulture) : "";
            var name = int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10";
            var display = k?.GetValue("DisplayVersion") as string;
            var edition = k?.GetValue("EditionID") as string;
            return $"{name} {display} {edition} (build {build}{ubr})".Replace("  ", " ", StringComparison.Ordinal);
        }
        catch (Exception) { return System.Environment.OSVersion.VersionString; }
    }

    private static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch (Exception) { return fallback; } }
}
