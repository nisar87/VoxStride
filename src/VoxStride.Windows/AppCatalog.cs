using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VoxStride.Core.Tools;

namespace VoxStride.Windows;

/// <summary>
/// Installed-app catalog built from Start menu shortcuts, App Paths registrations and a few built-ins.
/// No whole-disk scan. Shells and admin tools are excluded so the agent can never open a command interpreter.
/// </summary>
public sealed partial class WindowsAppCatalog : IAppCatalog
{
    private volatile IReadOnlyList<AppEntry> _apps = [];
    private DateTime _builtAt = DateTime.MinValue;
    private readonly Lock _gate = new();
    public bool IsReady { get; private set; }

    private static readonly HashSet<string> BlockedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wt.exe", "WindowsTerminal.exe", "regedit.exe", "mmc.exe", "wscript.exe", "cscript.exe",
        "mshta.exe", "rundll32.exe", "bash.exe", "wsl.exe", "taskmgr.exe", "msconfig.exe", "gpedit.msc", "control.exe", "diskpart.exe", "format.com",
    };

    private static readonly HashSet<string> BlockedNames = new(BlockedExecutables.Select(Path.GetFileNameWithoutExtension).OfType<string>(), StringComparer.OrdinalIgnoreCase)
    { "command prompt", "terminal", "windows terminal", "windows powershell", "registry editor", "task manager" };
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".msc", ".hta", ".scr", ".com" };

    /// <summary>Shells, script hosts and admin consoles are never launchable, whatever their shortcut is called.</summary>
    internal static bool IsBlocked(string name, string target, IEnumerable<string> aliases)
    {
        if (BlockedExecutables.Contains(Path.GetFileName(target)) || BlockedExtensions.Contains(Path.GetExtension(target))) return true;
        var lower = name.ToLowerInvariant();
        if (BlockedNames.Contains(lower) || lower.Contains("prompt", StringComparison.Ordinal) || lower.Contains("powershell", StringComparison.Ordinal) || lower.Contains("shell", StringComparison.Ordinal) && lower.Contains("command", StringComparison.Ordinal))
            return true;
        return aliases.Any(a => BlockedNames.Contains(a));
    }

    private static readonly string[] NoiseNames = ["uninstall", "readme", "read me", "help", "website", "documentation", "license", "release notes", "changelog", "manual", "support"];

    public Task BuildAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<AppEntry>();
        void Add(string name, string target, string source, params string[] aliases)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var lower = name.ToLowerInvariant();
            if (NoiseNames.Any(n => lower.Contains(n, StringComparison.Ordinal))) return;
            var al = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { lower, Simplify(lower) };
            foreach (var a in aliases) al.Add(a.ToLowerInvariant());
            if (IsBlocked(name, target, al)) return;
            list.Add(new AppEntry(source + ":" + target.ToLowerInvariant(), name, target, al.Where(a => a.Length > 0).ToList(), source));
        }

        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Add("Notepad", Path.Combine(sys, "notepad.exe"), "builtin", "notepad", "note pad");
        Add("Calculator", Path.Combine(sys, "calc.exe"), "builtin", "calculator", "calc");
        Add("Paint", Path.Combine(sys, "mspaint.exe"), "builtin", "paint", "ms paint");
        Add("File Explorer", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "builtin", "explorer", "file explorer", "files");
        Add("Settings", "ms-settings:", "builtin", "settings", "windows settings");

        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var programs = Path.Combine(root, "Programs");
            if (!Directory.Exists(programs)) continue;
            foreach (var lnk in Directory.EnumerateFiles(programs, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }))
            {
                ct.ThrowIfCancellationRequested();
                var target = ShellLink.ResolveTarget(lnk);
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (target is not null && IsBlocked(name, target, [])) continue;
                Add(name, lnk, "startmenu", target is null ? [] : [Path.GetFileNameWithoutExtension(target)]);
            }
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (key is null) continue;
            foreach (var exe in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(exe);
                var path = (sub?.GetValue(null) as string)?.Trim('"');
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                var name = Path.GetFileNameWithoutExtension(exe);
                Add(name, path, "apppaths", name);
            }
        }

        // Prefer Start menu entries, then App Paths, de-duplicated by display name.
        var deduped = list
            .GroupBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(a => a.Source switch { "builtin" => 0, "startmenu" => 1, _ => 2 }).First())
            .ToList();
        lock (_gate) { _apps = deduped; _builtAt = DateTime.UtcNow; IsReady = true; }
    }, ct);

    public IReadOnlyList<AppEntry> Snapshot() => _apps;

    public AppResolution Resolve(string name)
    {
        if (IsReady && DateTime.UtcNow - _builtAt > TimeSpan.FromMinutes(10)) _ = BuildAsync();
        return ResolveIn(_apps, name);
    }

    internal static AppResolution ResolveIn(IReadOnlyList<AppEntry> apps, string name)
    {
        var n = Simplify(name.Trim().ToLowerInvariant());
        n = Regex.Replace(n, @"^(the|my)\s+", "");
        n = Regex.Replace(n, @"\s+(app|application|program)$", "");
        if (n.Length == 0) return new AppResolution(null, []);
        if (n is "notepad plus plus" or "notepad plus" or "notepad ++") n = "notepad++";
        if (BlockedNames.Contains(n) || BlockedNames.Contains(n.Replace(".exe", "", StringComparison.OrdinalIgnoreCase))) return new AppResolution(null, []);

        var exact = apps.Where(a => a.Aliases.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return new AppResolution(exact[0], exact);
        if (exact.Count > 1)
        {
            var byName = exact.Where(a => Simplify(a.DisplayName.ToLowerInvariant()) == n).ToList();
            return byName.Count == 1 ? new AppResolution(byName[0], exact) : new AppResolution(null, exact);
        }
        var prefix = apps.Where(a => a.Aliases.Any(x => x.StartsWith(n + " ", StringComparison.OrdinalIgnoreCase))).ToList();
        if (prefix.Count == 1) return new AppResolution(prefix[0], prefix);
        if (prefix.Count > 1) return new AppResolution(null, prefix);
        return new AppResolution(null, []);
    }

    internal static string Simplify(string s)
    {
        s = Regex.Replace(s, @"\s*\((x64|x86|64-bit|32-bit|user|preview)\)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+\d+(\.\d+)*$", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}

/// <summary>Minimal IShellLinkW interop to resolve shortcut targets (read-only).</summary>
internal static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, nint pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    public static string? ResolveTarget(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, 0, 0x4 /* SLGP_RAWPATH */);
                var p = Environment.ExpandEnvironmentVariables(sb.ToString());
                return p.Length == 0 ? null : p;
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        catch (Exception) { return null; }
    }
}

/// <summary>Launches catalog apps and binds the resulting window by process id, process start time and HWND.</summary>
public sealed class WindowsAppController : IAppController
{
    public async Task<WindowIdentity?> LaunchAsync(AppEntry app, TimeSpan waitForWindow, CancellationToken ct)
    {
        var before = Native.TopLevelWindows(IsAppWindow).ToHashSet();
        var hint = ProcessHint(app);
        Process? started;
        try
        {
            started = Process.Start(new ProcessStartInfo(app.LaunchTarget) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException($"Windows could not start {app.DisplayName}: {ex.Message}"); }
        if (app.LaunchTarget.StartsWith("ms-settings:", StringComparison.Ordinal)) hint = "SystemSettings";

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < waitForWindow)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = Native.TopLevelWindows(IsAppWindow).Where(h => !before.Contains(h)).ToList();
            var match = candidates.FirstOrDefault(h => Matches(h, hint, app.DisplayName, started?.Id));
            if (match != 0) return Identity(match);
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        // Single-instance apps may reuse an existing window: bind it only if it matches the app.
        var existing = Native.TopLevelWindows(IsAppWindow).FirstOrDefault(h => Matches(h, hint, app.DisplayName, null));
        if (existing != 0) { Native.ShowWindow(existing, 9 /* SW_RESTORE */); Native.SetForegroundWindow(existing); return Identity(existing); }
        return null;
    }

    public Task<bool> IsAliveAsync(WindowIdentity w, CancellationToken ct)
    {
        if (!Native.IsWindow((nint)w.Hwnd) || Native.ProcessIdOf((nint)w.Hwnd) != (uint)w.ProcessId) return Task.FromResult(false);
        try
        {
            using var p = Process.GetProcessById(w.ProcessId);
            return Task.FromResult(!p.HasExited && p.StartTime.ToUniversalTime() == w.ProcessStartUtc);
        }
        catch (Exception) { return Task.FromResult(false); }
    }

    internal static bool IsAppWindow(nint h) => Native.IsWindowVisible(h) && Native.GetWindow(h, Native.GW_OWNER) == 0 && Native.Title(h).Length > 0;

    private static string? ProcessHint(AppEntry app)
    {
        var target = app.LaunchTarget.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShellLink.ResolveTarget(app.LaunchTarget) : app.LaunchTarget;
        return target is null ? null : Path.GetFileNameWithoutExtension(target);
    }

    private static bool Matches(nint h, string? hint, string displayName, int? pid)
    {
        var wpid = (int)Native.ProcessIdOf(h);
        if (pid is { } p && wpid == p) return true;
        string pname;
        try { using var proc = Process.GetProcessById(wpid); pname = proc.ProcessName; } catch (Exception) { return false; }
        if (hint is not null && pname.Equals(hint, StringComparison.OrdinalIgnoreCase)) return true;
        // Packaged app stubs (e.g. Windows 11 Notepad) start a different process with the same name.
        var first = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return pname.Contains(first, StringComparison.OrdinalIgnoreCase);
    }

    internal static WindowIdentity? Identity(nint h)
    {
        var pid = (int)Native.ProcessIdOf(h);
        try
        {
            using var p = Process.GetProcessById(pid);
            return new WindowIdentity(pid, p.StartTime.ToUniversalTime(), h, Native.Title(h), p.ProcessName);
        }
        catch (Exception) { return null; }
    }
}
