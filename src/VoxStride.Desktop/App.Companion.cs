using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VoxStride.Contracts;
using VoxStride.Core.Live;

namespace VoxStride.Desktop;

/// <summary>The live companion's lifetime: shown at startup, talking straight away, and owned by the app.</summary>
public partial class App
{
    private CompanionWindow? _companionWindow;
    private bool _liveStarting;
    private bool _yieldMuted;
    private TwitchChatClient? _twitch;
    private System.Windows.Threading.DispatcherTimer? _twitchTimer;

    public LiveCompanion? Companion { get; private set; }

    /// <summary>Called once the host has started.</summary>
    private void SetUpCompanion()
    {
        var h = Host!;
        // Offline / classic mode: the same character shows the classic pipeline when there's no Gemini key.
        string? lastReply = null;
        h.Conversation.StateChanged += s => Dispatcher.BeginInvoke(() => { if (Companion is null) _companionWindow?.ShowClassic(s, s == AgentState.Speaking ? lastReply : null); });
        h.Conversation.TranscriptChanged += (f, i) => Dispatcher.BeginInvoke(() => { if (Companion is null) _companionWindow?.ShowClassicHeard((f + " " + i).Trim()); });
        h.Events.Published += ev => { if (ev.State == AgentState.Speaking && ev.Message is { Length: > 0 } m && ev.Phase is not ("draft" or "waiting")) { lastReply = m; Dispatcher.BeginInvoke(() => { if (Companion is null) _companionWindow?.ShowClassic(AgentState.Speaking, m); }); } };
        h.LiveApprovals.Asking += q => Dispatcher.BeginInvoke(() => _companionWindow?.ShowQuestion(q.Title + " Say yes or no."));
        h.LiveApprovals.Answered += _ => Dispatcher.BeginInvoke(() => _companionWindow?.ClearQuestion());
        if (!Settings.LiveMode) { Log.Info("live", "live mode is off in settings"); return; }
        _companionWindow = new CompanionWindow(this);
        if (Settings.CompanionVisible) _companionWindow.Show();
        h.WakeToLive = said =>
        {
            if (!Settings.LiveMode || !h.CanTalkLive) return false;
            Dispatcher.BeginInvoke(async () => { await StartCompanionAsync(); if (said is { Length: > 2 } && Companion is { IsOn: true } c) await c.SendTextAsync(said); });
            return true;
        };
        h.MicYieldChanged += other => Dispatcher.BeginInvoke(() =>
        {
            if (Companion is not { IsOn: true } c) return;
            if (other is not null && !c.MicMuted) { _yieldMuted = true; c.SetMicMuted(true); _companionWindow?.Attach(c); _vm.AddActivity($"{other} is using the microphone, so I muted myself until it's done."); }
            else if (other is null && _yieldMuted) { _yieldMuted = false; c.SetMicMuted(false); _companionWindow?.Attach(c); }
        });
        if (h.CanTalkLive && Settings.LiveStartListening && Settings.MicrophoneConsent && !_muted) _ = StartCompanionAsync();
        else if (!h.CanTalkLive && Settings.ActiveProvider is not null) _companionWindow.ShowClassic(_muted ? AgentState.Muted : AgentState.Standby, null);
    }

    public async Task StartCompanionAsync()
    {
        if (Host is null || _liveStarting || Companion is { IsOn: true }) return;
        if (!Host.CanTalkLive) { ShowMain(3); _vm.AddActivity("Add a Gemini key under Reasoning providers to talk with me in real time."); return; }
        if (!Settings.MicrophoneConsent) { ShowMain(4); _vm.AddActivity("Enable the microphone under Privacy first."); return; }
        _liveStarting = true;
        try
        {
            if (_companionWindow is null) { _companionWindow = new CompanionWindow(this); }
            if (!_companionWindow.IsVisible) { Settings.CompanionVisible = true; _companionWindow.Show(); }
            if (Companion is not null) { await Companion.DisposeAsync(); Companion = null; }
            var c = Host.CreateCompanion();
            if (c is null) return;
            Companion = c;
            c.Usage += (input, output) => _ = Host.Store.RecordUsageAsync("GeminiLive", c.Model, input, output, DateTimeOffset.UtcNow, CancellationToken.None);
            _companionWindow.Attach(c);
            Host.LiveActive = true;
            await Host.StopWakeAsync();
            await c.StartAsync();
            if (c.State == CompanionState.Error)
            {
                Host.LiveActive = false;
                _ = Host.StartWakeAsync();
            }
            else
            {
                if (_muted) c.SetMicMuted(true);
                StartTwitch(c);
            }
        }
        catch (Exception ex) { Log.Error("live", "starting the companion failed", ex); }
        finally { _liveStarting = false; }
    }

    /// <summary>Starts the Twitch co-host if it's switched on: viewers' chat becomes untrusted context, never commands.</summary>
    private void StartTwitch(LiveCompanion c)
    {
        if (!Settings.TwitchCoHost || !TwitchIrc.IsValidChannel(Settings.TwitchChannel)) return;
        var host = new TwitchCoHost(() => c.IsQuiet, text => c.SendContextAsync(text), TimeProvider.System);
        _twitch = new TwitchChatClient();
        _twitch.Received += host.Add;
        _twitch.ConnectionChanged += on => Dispatcher.BeginInvoke(() => _vm.AddActivity(on ? $"Co-hosting #{TwitchIrc.NormalizeChannel(Settings.TwitchChannel!)} on Twitch." : "Twitch chat disconnected; reconnecting…"));
        _twitch.Start(Settings.TwitchChannel!);
        _twitchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _twitchTimer.Tick += async (_, _) => { try { await host.TickAsync(); } catch (Exception ex) { Log.Warn("twitch", "co-host tick failed", ex); } };
        _twitchTimer.Start();
    }

    private async Task StopTwitchAsync()
    {
        _twitchTimer?.Stop(); _twitchTimer = null;
        if (_twitch is not null) { await _twitch.DisposeAsync(); _twitch = null; }
    }

    public async Task StopCompanionAsync(string reason)
    {
        await StopTwitchAsync();
        if (Companion is null) return;
        var c = Companion;
        Companion = null;
        await c.DisposeAsync();
        _companionWindow?.Attach(null);
        if (Host is not null)
        {
            Host.LiveActive = false;
            if (!_muted) await Host.StartWakeAsync();
        }
        Log.Info("live", "conversation ended: " + reason);
    }

    /// <summary>Classic voice mode from the character's mic button (no Gemini key: wake word / dictation / offline AI).</summary>
    public void ClassicTalk() => TalkOrProceed();

    public Task ClassicTypedAsync(string text)
    {
        Host?.Conversation.SubmitTyped(text);
        _companionWindow?.ShowClassicHeard(text);
        return Task.CompletedTask;
    }

    public Task ToggleCompanionAsync() => Companion is { IsOn: true } ? StopCompanionAsync("ended by the user") : StartCompanionAsync();

    public async Task RestartCompanionAsync()
    {
        await StopCompanionAsync("restarting with new settings");
        await StartCompanionAsync();
    }

    public void ToggleCompanionMute()
    {
        if (Companion is not { IsOn: true } c) return;
        _yieldMuted = false;
        c.SetMicMuted(!c.MicMuted);
        _companionWindow?.Attach(c); // refresh the look
    }

    /// <summary>Switches to one of VoxStride's characters (look, voice and personality).</summary>
    public async Task ChooseCharacterAsync(string id)
    {
        var ch = CompanionCharacters.Get(id);
        Settings.CompanionCharacter = ch.Id;
        Settings.CompanionImagePath = null;
        Settings.CloudVoiceName = ch.Voice;
        Settings.Save();
        Log.Info("live", $"character changed to {ch.Name} (voice {ch.Voice})");
        _companionWindow?.ApplyLook();
        if (Companion is { IsOn: true }) await RestartCompanionAsync(); // new voice and personality
    }

    /// <summary>Lets the user pick a picture (PNG, JPG, GIF) to use as the companion; a copy is kept in VoxStride's folder.</summary>
    public async Task ChooseCompanionPictureAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a picture for your companion", Filter = "Pictures (*.png;*.jpg;*.jpeg;*.gif)|*.png;*.jpg;*.jpeg;*.gif" };
        if (dlg.ShowDialog() != true) return;
        var info = new System.IO.FileInfo(dlg.FileName);
        if (info.Length > 15 * 1024 * 1024) { MessageBox.Show("That picture is too big (over 15 MB).", "VoxStride"); return; }
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxStride");
        System.IO.Directory.CreateDirectory(dir);
        var target = System.IO.Path.Combine(dir, "companion-picture" + info.Extension.ToLowerInvariant());
        foreach (var old in System.IO.Directory.GetFiles(dir, "companion-picture.*")) { try { System.IO.File.Delete(old); } catch (System.IO.IOException) { } }
        System.IO.File.Copy(dlg.FileName, target, overwrite: true);
        Settings.CompanionImagePath = target;
        Settings.Save();
        Log.Info("live", "companion picture set (copied into VoxStride's folder)");
        _companionWindow?.ApplyLook();
        if (Companion is { IsOn: true }) await RestartCompanionAsync();
    }

    public void RemoveCompanionPicture()
    {
        Settings.CompanionImagePath = null;
        Settings.Save();
        _companionWindow?.ApplyLook();
    }

    public void ShowCompanion()
    {
        Settings.CompanionVisible = true; Settings.Save();
        _companionWindow ??= new CompanionWindow(this);
        _companionWindow.Show();
        if (Companion is not null) _companionWindow.Attach(Companion);
    }

    /// <summary>True when the hotkey was handled by the live companion.</summary>
    private bool LiveHotkey(int id)
    {
        if (!Settings.LiveMode || Host?.CanTalkLive != true) return false;
        switch (id)
        {
            case HK_TALK:
                if (Companion is not { IsOn: true }) _ = StartCompanionAsync();
                else ToggleCompanionMute();
                return true;
            case HK_STOP:
                if (Companion is { IsOn: true } c) { c.Hush(); return true; }
                return false;
            case HK_MUTE:
                if (Companion is { IsOn: true }) { ToggleCompanionMute(); return true; }
                return false;
        }
        return false;
    }
}
