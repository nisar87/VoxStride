using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using VoxStride.Contracts;

namespace VoxStride.Desktop;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private System.Windows.Forms.NotifyIcon? _tray;
    private HwndSource? _hotkeySink;
    private OverlayWindow? _overlay;
    private MainWindow? _main;
    private readonly AssistantViewModel _vm = new();
    private DispatcherTimer? _elapsedTimer;
    private bool _muted;
    private readonly UpdateService _updates = new();
    private DispatcherTimer? _updateTimer;
    public Core.Updates.UpdateInfo? AvailableUpdate { get; private set; }
    public event Action? UpdateStateChanged;

    public AppHost? Host { get; private set; }
    public AppSettings Settings { get; private set; } = new();
    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--smoke-test"))
        {
            var code = await SmokeTest.RunAsync(e.Args);
            Shutdown(code);
            return;
        }

        _singleInstance = new Mutex(true, @"Local\VoxStride.SingleInstance", out var first);
        if (!first)
        {
            // Another instance is running: it will surface itself via its tray icon.
            Shutdown(0);
            return;
        }

        Settings = AppSettings.Load();
        var approvals = new WpfApproval(Dispatcher, async (op, scope) =>
        {
            await Host!.Store.AddRuleAsync(op, scope, CancellationToken.None);
            await Host.Rules.RefreshAsync(CancellationToken.None);
        });
        Host = new AppHost(Settings, approvals, enableSpeech: true);
        WireEvents();
        CreateTray();
        RegisterHotkeys();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _overlay = new OverlayWindow(this, _vm);
        _main = new MainWindow(this, _vm);
        RefreshProviderLabel();
        if (!Settings.MicrophoneConsent) { _muted = true; Host.Conversation.SetMuted(true); }
        await Host.StartAsync(CancellationToken.None);

        var background = e.Args.Contains("--background") && Settings.FirstRunCompleted;
        if (!background) _main.Show();
        if (e.Args.Contains("--updated")) _vm.AddActivity($"VoxStride was updated to version {UpdateService.CurrentVersion.ToString(3)}.", "✔");
        if (Settings.ActiveProvider is not null && !Settings.CloudSpeechConsent && Settings.MicrophoneConsent)
            _vm.AddActivity("Tip: turn on Cloud speech in Voice settings — it understands accents and mixed Urdu/English much better.");
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += async (_, _) => await MaybeCheckForUpdatesAsync(false);
        _updateTimer.Start();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => _vm.TickElapsed();
        _elapsedTimer.Start();
    }

    private void WireEvents()
    {
        var h = Host!;
        h.Events.Published += ev => Dispatcher.BeginInvoke(() => _vm.Apply(ev));
        h.Conversation.StateChanged += s => Dispatcher.BeginInvoke(() => OnStateChanged(s));
        h.Conversation.TranscriptChanged += (f, i) => Dispatcher.BeginInvoke(() => { _vm.FinalTranscript = f; _vm.InterimTranscript = i; });
        h.Conversation.Notice += n => { DiagnosticsLog.Write("notice: " + n); Dispatcher.BeginInvoke(() => { _vm.Status = n; _vm.AddActivity(n); }); };
        h.Conversation.StateChanged += s => DiagnosticsLog.Write("state: " + s);
        h.Events.Published += ev => { if (ev.Phase is "result" or "no-provider" or "cancelled" or "recovery" or "wake-unavailable") DiagnosticsLog.Write($"{ev.Phase}: {ev.State}"); };
        h.Conversation.Committed += c => Dispatcher.BeginInvoke(() => _vm.AddActivity("You: " + c.FinalizedTranscript, "🗣"));
        h.Broker.Blocked += (tool, reason) => { DiagnosticsLog.Write($"blocked: {tool} {reason}"); Dispatcher.BeginInvoke(() => _vm.AddActivity($"Blocked {tool}: {reason}", "⛔")); };
        DispatcherUnhandledException += (_, a) => DiagnosticsLog.Write("ui-exception: " + a.Exception.GetType().Name + ": " + a.Exception.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, a) => DiagnosticsLog.Write("fatal: " + a.ExceptionObject);
    }

    private async void OnStateChanged(AgentState s)
    {
        _vm.State = s;
        ApplyOverlayVisibility();
        if (s == AgentState.Listening) { _vm.FinalTranscript = ""; _vm.InterimTranscript = ""; _vm.Status = "Speak naturally — pauses are fine. Say \"VoxStride, execute\" or press Proceed when done."; }
        Host?.SetConversationIdle(s is AgentState.Standby or AgentState.Muted);
        if (s == AgentState.Standby && Host is not null && !_muted) await Host.StartWakeAsync();
        if (s is AgentState.Listening) await (Host?.StopWakeAsync() ?? Task.CompletedTask);
    }

    public void ApplyOverlayVisibility()
    {
        if (_overlay is null) return;
        var active = _vm.State is not (AgentState.Standby or AgentState.Muted);
        if (Settings.ShowOverlay && active) { if (!_overlay.IsVisible) { _overlay.PositionTopRight(); _overlay.Show(); } }
        else if (_overlay.IsVisible && !active)
        {
            // Keep the result visible briefly after finishing.
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (_, _) => { t.Stop(); if (_vm.State is AgentState.Standby or AgentState.Muted) _overlay.Hide(); };
            t.Start();
        }
        else if (!Settings.ShowOverlay) _overlay.Hide();
    }

    // ---------- Updates ----------
    private bool _updateBusy;
    private DateTime _idleSince = DateTime.UtcNow;

    public async Task MaybeCheckForUpdatesAsync(bool userRequested)
    {
        if (_updateBusy) return;
        if (!userRequested)
        {
            if (_vm.State is not (AgentState.Standby or AgentState.Muted)) { _idleSince = DateTime.UtcNow; return; }
            if (AvailableUpdate is not null)
            {
                // Auto-install only after VoxStride has been idle for 2 minutes, so it never interrupts you.
                if (Settings.AutoInstallUpdates && DateTime.UtcNow - _idleSince > TimeSpan.FromMinutes(2)) await InstallUpdateAsync();
                return;
            }
            if (!Settings.AutoCheckUpdates || DateTimeOffset.UtcNow - Settings.LastUpdateCheck < TimeSpan.FromHours(6)) return;
        }
        _updateBusy = true;
        try
        {
            AvailableUpdate = await _updates.CheckAsync(CancellationToken.None);
            Settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            Settings.Save();
            if (AvailableUpdate is { } u)
            {
                _vm.AddActivity($"A new version ({u.Version.ToString(3)}) is available." + (Settings.AutoInstallUpdates ? " I'll install it when you're not using me." : " Open About to install it."), "⬆");
                _tray?.ShowBalloonTip(5000, "VoxStride update", $"Version {u.Version.ToString(3)} is ready.", System.Windows.Forms.ToolTipIcon.Info);
            }
            else if (userRequested) _vm.AddActivity($"You're on the latest version ({UpdateService.CurrentVersion.ToString(3)}).");
        }
        catch (Exception ex)
        {
            if (userRequested) _vm.AddActivity("I couldn't check for updates: " + ex.Message, "!");
        }
        finally { _updateBusy = false; UpdateStateChanged?.Invoke(); }
    }

    public async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } u || _updateBusy) return;
        if (Host?.Runs is not null && _vm.IsBusy) { _vm.AddActivity("I'll update after the current task finishes."); return; }
        _updateBusy = true;
        try
        {
            _vm.Status = $"Downloading VoxStride {u.Version.ToString(3)}…";
            var path = await _updates.DownloadAsync(u, new Progress<double>(p => _vm.Status = $"Downloading update… {p:0}%"), CancellationToken.None);
            _vm.AddActivity("Update verified. Installing — VoxStride will restart in a few seconds.", "⬆");
            UpdateService.LaunchInstaller(path);
            await Task.Delay(800);
            ExitApp();
        }
        catch (Exception ex)
        {
            _vm.AddActivity("The update didn't install: " + ex.Message, "!");
            _updateBusy = false;
        }
    }

    /// <summary>Restarts VoxStride (used after changing speech engine settings).</summary>
    public async void RestartApp()
    {
        IsExiting = true;
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        UnregisterHotkeys();
        if (Host is not null) await Host.DisposeAsync();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        _singleInstance = null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        Shutdown(0);
    }

    public void RefreshProviderLabel() =>
        _vm.ProviderLabel = Settings.ActiveProvider is { } k ? $"{k} · {Settings.Models.GetValueOrDefault(k)}" : "Local commands only";

    public void TalkOrProceed()
    {
        if (Host is null) return;
        if (_muted) { ToggleMute(); }
        if (_vm.IsListening) Host.Conversation.PressProceed();
        else Host.Conversation.ActivateFromHotkey();
    }

    public void ToggleMute()
    {
        if (Host is null) return;
        if (!Settings.MicrophoneConsent && _muted)
        {
            ShowMain(4);
            _vm.AddActivity("Enable the microphone under Privacy first.");
            return;
        }
        _muted = !_muted;
        Host.Conversation.SetMuted(_muted);
        _overlay?.SetMuted(_muted);
        if (_muted) _ = Host.StopWakeAsync(); else _ = Host.StartWakeAsync();
        if (_tray is not null) _tray.Text = _muted ? "VoxStride (muted)" : "VoxStride";
    }

    public void ShowMain(int page = 0)
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.ShowPage(page);
    }

    // ---------- Tray ----------
    private void CreateTray()
    {
        var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "voxstride.ico");
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.IO.File.Exists(icoPath) ? new System.Drawing.Icon(icoPath) : System.Drawing.SystemIcons.Application,
            Text = "VoxStride",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip(),
        };
        var menu = _tray.ContextMenuStrip;
        menu.Items.Add("Talk  (Ctrl+Alt+V)", null, (_, _) => Dispatcher.Invoke(TalkOrProceed));
        menu.Items.Add("Stop  (Ctrl+Alt+X)", null, (_, _) => Dispatcher.Invoke(() => Host?.Conversation.PressCancel()));
        menu.Items.Add("Mute / unmute  (Ctrl+Alt+M)", null, (_, _) => Dispatcher.Invoke(ToggleMute));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Open VoxStride", null, (_, _) => Dispatcher.Invoke(() => ShowMain()));
        menu.Items.Add("History", null, (_, _) => Dispatcher.Invoke(() => ShowMain(1)));
        menu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(() => ShowMain(2)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() => ShowMain(5));
        _tray.MouseClick += (_, a) => { if (a.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(() => ShowMain()); };
    }

    private async void ExitApp()
    {
        IsExiting = true;
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        UnregisterHotkeys();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (Host is not null) await Host.DisposeAsync();
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        Shutdown(0);
    }

    // ---------- Global hotkeys (work without network; stop is always available) ----------
    private const int HK_TALK = 1, HK_STOP = 2, HK_MUTE = 3;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    private void RegisterHotkeys()
    {
        var p = new HwndSourceParameters("VoxStrideHotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _hotkeySink = new HwndSource(p);
        _hotkeySink.AddHook(HotkeyHook);
        var ok = RegisterHotKey(_hotkeySink.Handle, HK_TALK, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x56 /* V */)
               & RegisterHotKey(_hotkeySink.Handle, HK_STOP, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x58 /* X */)
               & RegisterHotKey(_hotkeySink.Handle, HK_MUTE, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x4D /* M */);
        if (!ok) _vm.AddActivity("Some global shortcuts are used by another app. The tray menu and buttons still work.", "!");
    }

    private void UnregisterHotkeys()
    {
        if (_hotkeySink is null) return;
        var sink = _hotkeySink;
        _hotkeySink = null;
        UnregisterHotKey(sink.Handle, HK_TALK); UnregisterHotKey(sink.Handle, HK_STOP); UnregisterHotKey(sink.Handle, HK_MUTE);
        sink.Dispose();
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0312 /* WM_HOTKEY */) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case HK_TALK: TalkOrProceed(); break;
            case HK_STOP: Host?.Conversation.PressCancel(); break;
            case HK_MUTE: ToggleMute(); break;
        }
        return IntPtr.Zero;
    }

    // ---------- Lock / sleep ----------
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
        {
            Host?.Conversation.SetSessionLocked(true);
            _ = Host?.StopWakeAsync();
        }
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            Host?.Conversation.SetSessionLocked(false);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) { Host?.Conversation.SetSessionLocked(true); _ = Host?.StopWakeAsync(); }
        else if (e.Mode == PowerModes.Resume) Host?.Conversation.SetSessionLocked(false);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
