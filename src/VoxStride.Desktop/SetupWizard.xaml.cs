using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using VoxStride.Contracts;
using VoxStride.Core.Files;
using VoxStride.Core.Policy;
using VoxStride.Core.Tools;
using VoxStride.Providers;
using VoxStride.Speech;

namespace VoxStride.Desktop;

/// <summary>
/// First-run setup in four steps (spec §9, P6): 1 microphone/voice (speech, wake-word and voice tests), 2 AI provider
/// and key with a live connection test, 3 permissions (folders, wake word, screenshots, startup, logging), 4 a safe
/// read-only demo. Every step can be skipped; choices are saved on Finish (or Skip) and can be changed in Settings.
/// </summary>
public partial class SetupWizard : Window
{
    private readonly App _app;
    private int _step = 1;
    private const int Steps = 4;
    private static readonly string[] Titles = ["Microphone and voice", "AI provider (optional)", "Permissions", "Try it"];
    private readonly Dictionary<KnownFolder, CheckBox> _folders = new();

    public SetupWizard(App app)
    {
        _app = app;
        InitializeComponent();
        var s = app.Settings;
        var mics = new List<string> { WaveInRecorder.DescribeDevice(WaveInRecorder.WaveMapper) };
        mics.AddRange(WaveInRecorder.Devices());
        MicBox.ItemsSource = mics;
        MicBox.SelectedItem = s.MicrophoneName is { } mn && mics.Contains(mn) ? mn : mics[0];
        foreach (var f in FileScope.DefaultFolders)
        {
            var cb = new CheckBox { Content = f.ToString(), IsChecked = !s.DisabledKnownFolders.Contains(f.ToString(), StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 3, 16, 3) };
            _folders[f] = cb;
            FolderChecks.Children.Add(cb);
        }
        WakeBox.IsChecked = s.WakeWordEnabled;
        YieldBox.IsChecked = s.YieldMicToOtherApps;
        ScreenBox.IsChecked = s.ScreenCapture == ScreenCapturePolicy.Never;
        StartupBox.IsChecked = s.LaunchAtLogon;
        LogTextBox.IsChecked = s.LogConversationText;
        Log.Info("ui", "setup wizard opened");
        Show(1);
    }

    private void Show(int step)
    {
        _step = step;
        Step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        StepLabel.Text = $"Step {step} of {Steps}";
        StepTitle.Text = Titles[step - 1];
        StepProgress.Value = step;
        BackButton.IsEnabled = step > 1;
        NextButton.Content = step == Steps ? "Finish" : "Next";
        Log.Info("ui", $"setup step {step}: {Titles[step - 1]}");
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (_step > 1) Show(_step - 1); }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 3) Save(); // permissions apply before the demo runs
        if (_step < Steps) { Show(_step + 1); return; }
        Save();
        Log.Info("ui", "setup finished");
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("ui", $"setup skipped at step {_step}");
        Save();
        Close();
    }

    private string? SelectedMic => MicBox.SelectedIndex <= 0 ? null : MicBox.SelectedItem as string;

    private void Save()
    {
        var s = _app.Settings;
        var micBefore = s.MicrophoneName;
        s.FirstRunCompleted = true;
        s.PrivacyConsent = true;
        s.MicrophoneConsent = MicConsentBox.IsChecked == true;
        s.MicrophoneName = SelectedMic;
        s.DisabledKnownFolders = _folders.Where(p => p.Value.IsChecked != true).Select(p => p.Key.ToString()).ToList();
        s.WakeWordEnabled = WakeBox.IsChecked == true;
        s.YieldMicToOtherApps = YieldBox.IsChecked == true;
        s.ScreenCapture = ScreenBox.IsChecked == true ? ScreenCapturePolicy.Never : ScreenCapturePolicy.AskEachTime;
        s.LaunchAtLogon = StartupBox.IsChecked == true;
        s.LogConversationText = LogTextBox.IsChecked == true;
        Log.IncludeContent = s.LogConversationText;
        s.Save();
        var host = _app.Host!;
        try { host.ApplyStartup(s.LaunchAtLogon); } catch (Exception ex) { Log.Warn("ui", "could not update start-at-sign-in", ex); }
        if (!string.Equals(micBefore, s.MicrophoneName, StringComparison.Ordinal)) host.MicrophoneChanged();
        host.Conversation.SetMuted(!s.MicrophoneConsent);
        _app.SetMutedFromSetup(!s.MicrophoneConsent);
        _ = s.MicrophoneConsent && s.WakeWordEnabled ? host.StartWakeAsync() : host.StopWakeAsync();
    }

    // ---------- Step 1 ----------
    private async void TestSpeech_Click(object sender, RoutedEventArgs e)
    {
        TestSpeechButton.IsEnabled = false;
        try
        {
            Step1Result.Text = "Listening for 6 seconds — say something like \"open Notepad\"…";
            var r = await _app.Host!.TestSpeechAsync(SelectedMic, TimeSpan.FromSeconds(6),
                l => Dispatcher.BeginInvoke(() => MicLevel.Value = Math.Min(100, l / 60.0)),
                heard => Dispatcher.BeginInvoke(() => Step1Result.Text = $"Heard so far: \"{heard}\""), CancellationToken.None);
            Step1Result.Text = r.Buffers == 0 ? "✖ That microphone sent no audio. Pick another one above."
                : r.Peak < VoxStride.Core.Voice.MicHealthCheck.SilenceThreshold ? "✖ No sound at all. Check the microphone isn't muted, or pick another one."
                : r.Heard.Length > 0 ? $"✔ I heard: \"{r.Heard}\""
                : "⚠ The microphone works, but I didn't catch any words. Speak a little closer, or try again.";
        }
        catch (Exception ex) { Step1Result.Text = "✖ " + ex.Message; Log.Error("ui", "setup speech test failed", ex); }
        finally { MicLevel.Value = 0; TestSpeechButton.IsEnabled = true; }
    }

    private async void TestWake_Click(object sender, RoutedEventArgs e)
    {
        TestWakeButton.IsEnabled = false;
        try
        {
            if (SelectedMic != _app.Settings.MicrophoneName) { _app.Settings.MicrophoneName = SelectedMic; _app.Settings.Save(); _app.Host!.MicrophoneChanged(); }
            Step1Result.Text = "Say \"Hey VoxStride\" now (8 seconds)…";
            var ok = await _app.Host!.TestWakeAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
            Step1Result.Text = ok ? "✔ I heard \"Hey VoxStride\"." : "⚠ I didn't hear it. Try speaking a little slower, or use Ctrl+Alt+V — it always works.";
        }
        catch (Exception ex) { Step1Result.Text = "✖ " + ex.Message; Log.Error("ui", "setup wake test failed", ex); }
        finally { TestWakeButton.IsEnabled = true; }
    }

    private void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Host!.Speech is { } sp) _ = sp.SpeakAsync("Hi! I'm VoxStride. Tell me what you need, and say VoxStride, execute when you're done.", CancellationToken.None);
        else Step1Result.Text = "No audio output device was found, so I'll show my answers as text.";
    }

    // ---------- Step 2 ----------
    private async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderBox.SelectedItem is not ComboBoxItem { Tag: string tag } || tag.Length == 0) { Step2Result.Text = "Choose a provider first, or skip this step."; return; }
        var kind = Enum.Parse<ProviderKind>(tag);
        var key = KeyBox.Password.Trim();
        if (key.Length == 0) { Step2Result.Text = "Paste your API key first."; return; }
        TestKeyButton.IsEnabled = false;
        Step2Result.Text = "Testing…";
        try
        {
            var host = _app.Host!;
            host.Secrets.Save(ProviderSelector.SecretName(kind), key);
            KeyBox.Clear();
            Log.Info("app", $"{kind} API key saved from setup (value never logged)");
            var model = _app.Settings.Models.GetValueOrDefault(kind, ProviderFactory.DefaultModel(kind));
            var r = await ProviderFactory.Create(new ProviderSettings(kind, model, key)).CheckAsync(CancellationToken.None);
            if (r.Ok)
            {
                _app.Settings.ActiveProvider = kind;
                _app.Settings.Save();
                host.Providers.Invalidate();
                _app.RefreshProviderLabel();
                Step2Result.Text = $"✔ {r.Message} {kind} is now your active provider.";
            }
            else Step2Result.Text = "✖ " + r.Message;
        }
        catch (Exception ex) { Step2Result.Text = "✖ " + ex.Message; Log.Error("ui", "setup key test failed", ex); }
        finally { TestKeyButton.IsEnabled = true; }
    }

    // ---------- Step 4 ----------
    private void Demo_Click(object sender, RoutedEventArgs e)
    {
        var host = _app.Host!;
        DemoButton.IsEnabled = false;
        Step4Result.Text = "Working…";
        void OnEvent(TaskEvent ev)
        {
            if (ev.Phase != "done") return;
            host.Events.Published -= OnEvent;
            Dispatcher.BeginInvoke(() => { Step4Result.Text = "✔ " + ev.Message; DemoButton.IsEnabled = true; });
        }
        host.Events.Published += OnEvent;
        host.Conversation.SubmitTyped("How much space is left on C?");
    }
}
