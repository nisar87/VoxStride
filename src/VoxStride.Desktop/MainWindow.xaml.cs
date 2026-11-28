using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoxStride.Contracts;
using VoxStride.Core.Files;
using VoxStride.Providers;
using VoxStride.Speech;

namespace VoxStride.Desktop;

public partial class MainWindow : Window
{
    private readonly App _app;
    private readonly AssistantViewModel _vm;
    private readonly FrameworkElement[] _pages;

    public MainWindow(App app, AssistantViewModel vm)
    {
        _app = app;
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        _pages = [HomePage, HistoryPage, ProvidersPage, VoicePage, PrivacyPage, AboutPage];
        Nav.SelectedIndex = 0;
        WelcomePanel.Visibility = Visibility.Collapsed; // first run uses the setup wizard
        Loaded += (_, _) => { LoadSettingsIntoUi(); CommandBox.Focus(); };
        app.UpdateStateChanged += () => Dispatcher.BeginInvoke(RefreshUpdateUi);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window keeps VoxStride in the tray; Exit is in the tray menu.
        if (!_app.IsExiting) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages is null) return;
        for (var i = 0; i < _pages.Length; i++) _pages[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        if (Nav.SelectedIndex == 1) _ = RefreshHistoryAsync();
        if (Nav.SelectedIndex == 2) _ = RefreshUsageAsync();
        if (Nav.SelectedIndex == 4) _ = RefreshRulesAsync();
        if (Nav.SelectedIndex == 5) RefreshDiagnostics();
    }

    public void ShowPage(int index) { Nav.SelectedIndex = index; }

    /// <summary>Re-reads settings into every page (after the setup wizard).</summary>
    public void ReloadSettings() => LoadSettingsIntoUi();

    private void RunSetup_Click(object sender, RoutedEventArgs e) => _app.ShowSetup();

    // ---------- Home ----------
    private void Welcome_Continue(object sender, RoutedEventArgs e) => CompleteWelcome(ConsentBox.IsChecked == true);
    private void Welcome_TypedOnly(object sender, RoutedEventArgs e) => CompleteWelcome(false);

    private void CompleteWelcome(bool mic)
    {
        Log.Info("ui", $"welcome completed microphone={(mic ? "enabled" : "typed only")}");
        var s = _app.Settings;
        s.FirstRunCompleted = true;
        s.PrivacyConsent = true;
        s.MicrophoneConsent = mic;
        s.Save();
        WelcomePanel.Visibility = Visibility.Collapsed;
        _vm.AddActivity(mic ? "Microphone enabled. Say \"Hey VoxStride\" or press Ctrl+Alt+V." : "Typed-only mode. You can enable the microphone under Privacy.");
        if (mic) _ = _app.Host!.StartWakeAsync();
        if (s.ActiveProvider is null) _vm.AddActivity("Tip: add a Claude, Gemini or OpenAI key under Reasoning providers for writing, browsing and open-ended tasks.");
        LoadSettingsIntoUi();
    }

    private void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { RunTyped(); e.Handled = true; }
    }

    private void Run_Click(object sender, RoutedEventArgs e) => RunTyped();

    private void RunTyped()
    {
        var text = CommandBox.Text.Trim();
        if (text.Length == 0) return;
        Log.Info("ui", $"typed command submitted while {(_vm.IsListening ? "listening (replaces the draft and proceeds)" : "idle")} text={Log.Text(text)}");
        if (_vm.IsListening) { _app.Host!.Conversation.EditTranscript(text); _app.Host.Conversation.PressProceed(); }
        else _app.Host!.Conversation.SubmitTyped(text);
        CommandBox.Clear();
    }

    private void Example_Click(object sender, RoutedEventArgs e)
    {
        CommandBox.Text = (string)((Button)sender).Content;
        CommandBox.Focus();
        CommandBox.CaretIndex = CommandBox.Text.Length;
    }

    private void Talk_Click(object sender, RoutedEventArgs e) { Log.Info("ui", "Talk button"); _app.TalkOrProceed(); }
    private void Pause_Click(object sender, RoutedEventArgs e) { Log.Info("ui", "Pause button"); _app.TogglePause(); }
    private void Stop_Click(object sender, RoutedEventArgs e) { Log.Info("ui", "Stop button"); _app.Host?.Conversation.PressCancel(); }
    private void Proceed_Click(object sender, RoutedEventArgs e) { Log.Info("ui", "Proceed button"); _app.Host?.Conversation.PressProceed(); }

    // ---------- History ----------
    public sealed record HistoryRow(string Badge, string When, string Meta, string Title, string Summary, string Steps);

    private async Task RefreshHistoryAsync()
    {
        var items = await _app.Host!.Store.GetHistoryAsync(100, CancellationToken.None);
        HistoryList.ItemsSource = items.Select(h => new HistoryRow(
            h.Success ? "✔" : "✖",
            h.StartedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            $"{h.Route}{(h.Provider is null ? "" : " · " + h.Provider)}{(h.InputTokens + h.OutputTokens > 0 ? $" · {h.InputTokens + h.OutputTokens:N0} tokens" : "")}",
            h.Transcript ?? "(instruction not kept)",
            h.Summary,
            string.Join("   ", h.Steps.Select(s => $"{(s.Verified ? "✓" : s.Outcome == ToolOutcome.Success ? "·" : "✗")} {s.Tool} ({s.DurationMs:0} ms)")))).ToList();
    }

    private void RefreshHistory_Click(object sender, RoutedEventArgs e) => _ = RefreshHistoryAsync();

    private void UndoLast_Click(object sender, RoutedEventArgs e)
    {
        _app.Host!.Conversation.SubmitTyped("undo the last organization");
        ShowPage(0);
    }

    /// <summary>Saves a zip with the logs, a PC/settings summary and redacted run history, for troubleshooting.</summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save VoxStride diagnostics report",
            FileName = $"VoxStride-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "Zip archive (*.zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var items = await _app.Host!.Store.GetHistoryAsync(200, CancellationToken.None);
            var runs = JsonSerializer.Serialize(items.Select(h => new
            {
                h.RunId, h.StartedAt, h.Route, h.Provider, h.Success, h.InputTokens, h.OutputTokens,
                Steps = h.Steps.Select(st => new { st.Tool, Outcome = st.Outcome.ToString(), st.Verified, st.DurationMs }),
            }), new JsonSerializerOptions { WriteIndented = true });
            var path = await Task.Run(() => DiagnosticsReport.Export(dlg.FileName, _app.Settings, _app.Host, ("runs.json", runs), ("diagnostics-view.txt", Diagnostics())));
            _vm.AddActivity("Saved the diagnostics report to " + path + ". It has no API keys, audio or files.");
        }
        catch (Exception ex) { Log.Error("ui", "saving the diagnostics report failed", ex); _vm.AddActivity("Couldn't save the diagnostics report: " + ex.Message, "!"); }
    }

    // ---------- Providers ----------
    private void LoadSettingsIntoUi()
    {
        var s = _app.Settings;
        foreach (ComboBoxItem item in ActiveProviderBox.Items)
            if ((string)item.Tag == (s.ActiveProvider?.ToString() ?? "")) ActiveProviderBox.SelectedItem = item;
        foreach (ComboBoxItem item in FallbackProviderBox.Items)
            if ((string)item.Tag == (s.FallbackProvider?.ToString() ?? "")) FallbackProviderBox.SelectedItem = item;
        BuildProviderCards();
        DailyBudgetBox.Text = s.DailyTokenBudget.ToString(CultureInfo.InvariantCulture);
        RunBudgetBox.Text = s.MaxTokensPerRun.ToString(CultureInfo.InvariantCulture);
        MaxStepsBox.Text = s.MaxSteps.ToString(CultureInfo.InvariantCulture);

        foreach (ComboBoxItem item in TalkKeyBox.Items) if ((string)item.Tag == s.TalkKey.ToString()) TalkKeyBox.SelectedItem = item;
        NaturalBox.IsChecked = s.NaturalMode;
        WakeBox.IsChecked = s.WakeWordEnabled; YieldBox.IsChecked = s.YieldMicToOtherApps; PlainProceedBox.IsChecked = s.AllowPlainProceed; SpeakBox.IsChecked = s.SpeakResults;
        CuesBox.IsChecked = s.AudioCues; OverlayBox.IsChecked = s.ShowOverlay;
        var cultures = SpeechEnvironment.RecognizerCultures().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        CultureBox.ItemsSource = cultures.Count > 0 ? cultures : new List<string> { s.SpeechCulture };
        CultureBox.SelectedItem = cultures.Contains(s.SpeechCulture) ? s.SpeechCulture : cultures.FirstOrDefault();
        var voices = SpeechEnvironment.Voices();
        VoiceBox.ItemsSource = voices;
        VoiceBox.SelectedItem = s.Voice is not null && voices.Contains(s.Voice) ? s.Voice : voices.FirstOrDefault();
        VoiceInfo.Text = cultures.Count == 0
            ? "No Windows speech recognizer is installed. Install an English speech pack (Settings › Time & language › Speech) to use voice. Typed commands still work."
            : $"Installed recognizers: {string.Join(", ", cultures)}. Voices: {voices.Count}.";

        var mics = new List<string> { WaveInRecorder.DescribeDevice(WaveInRecorder.WaveMapper) };
        mics.AddRange(WaveInRecorder.Devices());
        MicBox.ItemsSource = mics;
        MicBox.SelectedItem = s.MicrophoneName is { } mn && mics.Contains(mn) ? mn : mics[0];
        CloudSpeechBox.IsChecked = s.CloudSpeechConsent;
        foreach (ComboBoxItem item in EngineBox.Items) if ((string)item.Tag == s.SpeechEngine.ToString()) EngineBox.SelectedItem = item;
        SensitivitySlider.Value = s.MicSensitivity;
        NaturalVoiceBox.IsChecked = s.NaturalVoice;
        var hasOpenAi = _app.Host!.Secrets.Load(ProviderSelector.SecretName(ProviderKind.OpenAI)) is { Length: > 0 };
        var hasGemini = _app.Host.Secrets.Load(ProviderSelector.SecretName(ProviderKind.Gemini)) is { Length: > 0 };
        var cloudVoices = (hasGemini ? GeminiVoice.Voices : Array.Empty<string>()).Concat(hasOpenAi || !hasGemini ? OpenAIVoice.Voices : Array.Empty<string>()).ToList();
        CloudVoiceBox.ItemsSource = cloudVoices.Select(v => new ComboBoxItem { Content = GeminiVoice.Describe(v), Tag = v }).ToList();
        CloudVoiceBox.SelectedItem = CloudVoiceBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.CloudVoiceName) ?? CloudVoiceBox.Items[0];
        EngineInfo.Text = $"Currently using: {_app.Host.SpeechEngineLabel}. " + (_app.Host.UsingBasicEngine
            ? (!hasOpenAi && !hasGemini
                ? "The offline Windows engine works without a key but is weak with accents. For accurate recognition of your accent and mixed Urdu/English, add an OpenAI or Gemini key under Reasoning providers and tick cloud speech above. (A Claude key can't transcribe audio.)"
                : "Tick \"Use cloud speech recognition\" above for much better accuracy with accents.")
            : "Your voice is sent for transcription only while you're giving an instruction.");
        AutoUpdateBox.IsChecked = s.AutoCheckUpdates;
        AutoInstallBox.IsChecked = s.AutoInstallUpdates;
        RefreshUpdateUi();
        FolderPermChecks.Children.Clear();
        foreach (var f in VoxStride.Core.Tools.FileScope.DefaultFolders)
            FolderPermChecks.Children.Add(new CheckBox { Content = f.ToString(), Tag = f.ToString(), Margin = new Thickness(0, 3, 16, 3), IsChecked = !s.DisabledKnownFolders.Contains(f.ToString(), StringComparer.OrdinalIgnoreCase) });
        ExtraFoldersList.ItemsSource = s.AllowedFolders.ToList();
        BlockedAppsBox.Text = string.Join(Environment.NewLine, s.BlockedApps);
        BlockedSitesBox.Text = string.Join(Environment.NewLine, s.BlockedSites);
        ScreenPolicyBox.SelectedItem = ScreenPolicyBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.ScreenCapture.ToString()) ?? ScreenPolicyBox.Items[0];
        LiveModeBox.IsChecked = s.LiveMode; LiveStartBox.IsChecked = s.LiveStartListening; LiveSpeakersBox.IsChecked = s.LiveUsingSpeakers;
        UserNameBox.Text = s.UserName ?? ""; PersonalityBox.Text = s.CompanionPersonality ?? "";
        RefreshMemoryList();
        _ = RefreshLiveUsageAsync();
        CharacterBox.ItemsSource = CompanionCharacters.All;
        CharacterBox.SelectedItem = CompanionCharacters.Get(s.CompanionCharacter);
        RemovePictureButton.Visibility = s.CompanionImagePath is null ? Visibility.Collapsed : Visibility.Visible;
        UseMyBrowserBox.IsChecked = s.UseMyBrowser;
        RefreshExtensionStatus();
        MicConsentBox.IsChecked = s.MicrophoneConsent; RetainBox.IsChecked = s.RetainTranscripts; StartupBox.IsChecked = s.LaunchAtLogon; LogTextBox.IsChecked = s.LogConversationText;
        _app.RefreshProviderLabel();
    }

    private void ActiveProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ActiveProviderBox.SelectedItem is not ComboBoxItem item || !IsLoaded) return;
        var tag = (string)item.Tag;
        _app.Settings.ActiveProvider = tag.Length == 0 ? null : Enum.Parse<ProviderKind>(tag);
        _app.Settings.Save();
        _app.Host!.Providers.Invalidate();
        _app.RefreshProviderLabel();
    }

    private void FallbackProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FallbackProviderBox.SelectedItem is not ComboBoxItem item || !IsLoaded) return;
        var tag = (string)item.Tag;
        _app.Settings.FallbackProvider = tag.Length == 0 ? null : Enum.Parse<ProviderKind>(tag);
        _app.Settings.Save();
    }

    private void BuildProviderCards()
    {
        ProviderCards.Items.Clear();
        foreach (var kind in new[] { ProviderKind.Claude, ProviderKind.Gemini, ProviderKind.OpenAI })
        {
            var hasKey = _app.Host!.Secrets.Load(ProviderSelector.SecretName(kind)) is { Length: > 0 };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = kind switch { ProviderKind.Claude => "Claude", ProviderKind.Gemini => "Gemini", _ => "OpenAI" }, FontWeight = FontWeights.SemiBold, FontSize = 15 });
            var status = new TextBlock { Text = hasKey ? "Key saved in Credential Manager" : "No key saved", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 2, 0, 8) };
            panel.Children.Add(status);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(new TextBlock { Text = "Model", VerticalAlignment = VerticalAlignment.Center });
            var model = new ComboBox { IsEditable = true, ItemsSource = ProviderFactory.SuggestedModels(kind), Text = _app.Settings.Models.GetValueOrDefault(kind, ProviderFactory.DefaultModel(kind)), Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetColumn(model, 1); grid.Children.Add(model);
            var keyLabel = new TextBlock { Text = "API key", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(keyLabel, 1); grid.Children.Add(keyLabel);
            var key = new PasswordBox { Margin = new Thickness(0, 2, 0, 2) };
            AutomationPropertiesHelper.SetName(key, kind + " API key");
            Grid.SetRow(key, 1); Grid.SetColumn(key, 1); grid.Children.Add(key);
            panel.Children.Add(grid);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var save = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton") };
            var test = new Button { Content = "Test connection", Style = (Style)FindResource("PlainButton"), Margin = new Thickness(8, 0, 0, 0) };
            var remove = new Button { Content = "Remove key", Style = (Style)FindResource("PlainButton"), Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(save); buttons.Children.Add(test); buttons.Children.Add(remove);
            panel.Children.Add(buttons);

            save.Click += (_, _) =>
            {
                _app.Settings.Models[kind] = model.Text.Trim();
                if (key.Password.Length > 0)
                {
                    try { _app.Host!.Secrets.Save(ProviderSelector.SecretName(kind), key.Password.Trim()); key.Clear(); status.Text = "Key saved in Credential Manager"; Log.Info("app", $"{kind} API key saved (value never logged)"); }
                    catch (InvalidOperationException ex) { status.Text = ex.Message; }
                }
                if (_app.Settings.ActiveProvider is null && _app.Host!.Secrets.Load(ProviderSelector.SecretName(kind)) is { Length: > 0 }) _app.Settings.ActiveProvider = kind;
                _app.Settings.Save();
                _app.Host!.Providers.Invalidate();
                _ = RefreshEngineAfterKeyAsync();
                LoadSettingsIntoUi();
            };
            test.Click += async (_, _) =>
            {
                var k = key.Password.Length > 0 ? key.Password.Trim() : _app.Host!.Secrets.Load(ProviderSelector.SecretName(kind));
                if (string.IsNullOrEmpty(k)) { status.Text = "Enter a key first."; return; }
                status.Text = "Testing…";
                var r = await ProviderFactory.Create(new ProviderSettings(kind, model.Text.Trim(), k)).CheckAsync(CancellationToken.None);
                status.Text = (r.Ok ? "✔ " : "✖ ") + r.Message;
            };
            remove.Click += (_, _) =>
            {
                _app.Host!.Secrets.Delete(ProviderSelector.SecretName(kind));
                Log.Info("app", $"{kind} API key removed");
                if (_app.Settings.ActiveProvider == kind) _app.Settings.ActiveProvider = null;
                _app.Settings.Save();
                _app.Host!.Providers.Invalidate();
                _ = RefreshEngineAfterKeyAsync();
                LoadSettingsIntoUi();
            };
            ProviderCards.Items.Add(new Border { Child = panel, Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(10), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x10, 0x80, 0x80, 0x80)) });
        }
    }

    private void SaveLimits_Click(object sender, RoutedEventArgs e)
    {
        var s = _app.Settings;
        if (long.TryParse(DailyBudgetBox.Text, out var d) && d >= 0) s.DailyTokenBudget = d;
        if (long.TryParse(RunBudgetBox.Text, out var r) && r > 0) s.MaxTokensPerRun = r;
        if (int.TryParse(MaxStepsBox.Text, out var m) && m is > 0 and <= 100) s.MaxSteps = m;
        s.Save();
        _vm.AddActivity("Limits saved. Step and per-task limits apply after restart.");
    }

    private async Task RefreshUsageAsync()
    {
        var sb = new StringBuilder("Today's provider-reported usage: ");
        foreach (var k in new[] { ProviderKind.Claude, ProviderKind.Gemini, ProviderKind.OpenAI })
        {
            var (i, o) = await _app.Host!.Store.GetUsageSinceAsync(new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero), k.ToString(), CancellationToken.None);
            sb.Append(CultureInfo.InvariantCulture, $"{k} {i:N0} in / {o:N0} out   ");
        }
        UsageText.Text = sb.ToString();
    }

    // ---------- Voice ----------
    private async void SaveVoice_Click(object sender, RoutedEventArgs e)
    {
        var s = _app.Settings;
        var wakeChanged = s.WakeWordEnabled != (WakeBox.IsChecked == true);
        s.WakeWordEnabled = WakeBox.IsChecked == true; s.YieldMicToOtherApps = YieldBox.IsChecked == true; s.AllowPlainProceed = PlainProceedBox.IsChecked == true;
        var characterBefore = s.CompanionCharacter;
        var liveBefore = (s.LiveMode, s.LiveUsingSpeakers, s.UserName, s.CompanionPersonality, s.CloudVoiceName);
        s.LiveMode = LiveModeBox.IsChecked == true; s.LiveStartListening = LiveStartBox.IsChecked == true; s.LiveUsingSpeakers = LiveSpeakersBox.IsChecked == true;
        s.UserName = UserNameBox.Text.Trim() is { Length: > 0 } un ? un : null;
        s.CompanionPersonality = PersonalityBox.Text.Trim() is { Length: > 0 } pe ? pe : null;
        s.SpeakResults = SpeakBox.IsChecked == true; s.AudioCues = CuesBox.IsChecked == true; s.ShowOverlay = OverlayBox.IsChecked == true;
        if (VoiceBox.SelectedItem is string v) s.Voice = v;
        if (TalkKeyBox.SelectedItem is ComboBoxItem { Tag: string tk }) s.TalkKey = Enum.Parse<TalkKeyMode>(tk);
        var naturalBefore = s.NaturalMode;
        s.NaturalMode = NaturalBox.IsChecked == true;
        var engineBefore = (s.CloudSpeechConsent, s.SpeechEngine, s.MicSensitivity, s.SpeechCulture, naturalBefore);
        var micBefore = s.MicrophoneName;
        s.MicrophoneName = MicBox.SelectedIndex <= 0 ? null : MicBox.SelectedItem as string;
        if (CultureBox.SelectedItem is string c) s.SpeechCulture = c;
        s.CloudSpeechConsent = CloudSpeechBox.IsChecked == true;
        if (EngineBox.SelectedItem is ComboBoxItem { Tag: string tag }) s.SpeechEngine = Enum.Parse<SpeechEngine>(tag);
        s.MicSensitivity = SensitivitySlider.Value;
        s.NaturalVoice = NaturalVoiceBox.IsChecked == true;
        if (CloudVoiceBox.SelectedItem is ComboBoxItem { Tag: string cv }) s.CloudVoiceName = cv;
        s.Save();
        _app.Host!.RefreshVoice();
        if (!string.Equals(micBefore, s.MicrophoneName, StringComparison.Ordinal)) _app.Host.MicrophoneChanged(); // applies immediately
        if (engineBefore.SpeechCulture != s.SpeechCulture || engineBefore.naturalBefore != s.NaturalMode)
        {
            if (MessageBox.Show(this, "The new language or natural-mode setting takes effect after VoxStride restarts. Restart now?", "VoxStride", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            { _app.RestartApp(); return; }
        }
        else if (engineBefore != (s.CloudSpeechConsent, s.SpeechEngine, s.MicSensitivity, s.SpeechCulture, s.NaturalMode))
        {
            await _app.Host.RefreshSpeechEngineAsync(force: true); // engine and sensitivity apply without a restart
            _vm.AddActivity($"Speech engine: {_app.Host.SpeechEngineLabel}.");
        }
        if (_app.Host!.Speech is { } sp) { sp.Enabled = s.SpeakResults; sp.CuesEnabled = s.AudioCues; }
        if (wakeChanged) { if (s.WakeWordEnabled) await _app.Host.StartWakeAsync(); else await _app.Host.StopWakeAsync(); }
        _app.ApplyOverlayVisibility();
        if (CharacterBox.SelectedItem is CompanionCharacter ch && ch.Id != characterBefore)
        {
            await _app.ChooseCharacterAsync(ch.Id); // also sets its voice and restarts the conversation
            LoadSettingsIntoUi();
        }
        else if (liveBefore != (s.LiveMode, s.LiveUsingSpeakers, s.UserName, s.CompanionPersonality, s.CloudVoiceName))
        {
            if (!s.LiveMode) await _app.StopCompanionAsync("live mode turned off");
            else if (_app.Companion is { IsOn: true }) await _app.RestartCompanionAsync(); // new voice, name or personality
            else if (!liveBefore.LiveMode) { _app.ShowCompanion(); await _app.StartCompanionAsync(); }
        }
        _vm.AddActivity("Voice settings saved.");
    }

    private void ForgetMemory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Forget everything you asked VoxStride to remember?", "VoxStride", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _app.Host?.Memory.Clear();
        RefreshMemoryList();
        Log.Info("memory", "all remembered facts cleared by the user");
    }

    private async void CompanionPicture_Click(object sender, RoutedEventArgs e)
    {
        await _app.ChooseCompanionPictureAsync();
        RemovePictureButton.Visibility = _app.Settings.CompanionImagePath is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RemovePicture_Click(object sender, RoutedEventArgs e)
    {
        _app.RemoveCompanionPicture();
        RemovePictureButton.Visibility = Visibility.Collapsed;
    }

    private void ForgetSelected_Click(object sender, RoutedEventArgs e)
    {
        if (MemoryList.SelectedItem is not ListBoxItem { Tag: string text }) return;
        _app.Host?.Memory.Remove(text);
        RefreshMemoryList();
        Log.Info("memory", "one remembered fact removed by the user");
    }

    private void RefreshMemoryList()
    {
        var facts = _app.Host?.Memory.All() ?? [];
        MemoryCountText.Text = facts.Count == 0 ? "VoxStride doesn't remember anything about you yet. Say \"remember that…\" to teach it." : $"What VoxStride remembers about you ({facts.Count}, kept only on this PC):";
        MemoryList.ItemsSource = facts.Select(f => new ListBoxItem { Content = f.Text, Tag = f.Text, ToolTip = "Saved " + f.Saved.ToString("d MMM yyyy", CultureInfo.CurrentCulture) }).ToList();
    }

    private async Task RefreshLiveUsageAsync()
    {
        if (_app.Host is null) return;
        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var (i, o) = await _app.Host.Store.GetUsageSinceAsync(today, "GeminiLive", CancellationToken.None);
        LiveUsageText.Text = i + o == 0 ? "Live conversation hasn't used any Gemini tokens today." :
            $"Live conversation today: about {(i + o):N0} Gemini tokens ({i:N0} in, {o:N0} out). Check your limits at aistudio.google.com.";
    }

    /// <summary>A new OpenAI or Gemini key can enable cloud speech straight away; no restart needed.</summary>
    private async Task RefreshEngineAfterKeyAsync()
    {
        try
        {
            var before = _app.Host!.SpeechEngineLabel;
            await _app.Host.RefreshSpeechEngineAsync();
            _app.Host.RefreshVoice(); // a Gemini or OpenAI key also enables the natural voice
            if (_app.Settings.LiveMode && _app.Host.CanTalkLive && _app.Companion is not { IsOn: true })
            {
                _app.ShowCompanion();
                await _app.StartCompanionAsync(); // a new Gemini key: talk in real time straight away
            }
            if (_app.Host.SpeechEngineLabel != before) _vm.AddActivity($"Speech engine is now {_app.Host.SpeechEngineLabel}.");
            LoadSettingsIntoUi();
        }
        catch (Exception ex) { Log.Error("speech", "switching the speech engine failed", ex); }
    }

    private void TestVoice_Click(object sender, RoutedEventArgs e) =>
        _ = _app.Host!.Speech?.SpeakAsync("Hi! I'm VoxStride. Just tell me what you need, take your time, and say VoxStride, execute when you're done.", CancellationToken.None);

    private void Restart_Click(object sender, RoutedEventArgs e) => _app.RestartApp();

    private bool _micTesting;

    /// <summary>
    /// End-to-end speech test: listens on the selected microphone with the real speech engine for 6 seconds and shows
    /// the words it understood. A working level bar alone doesn't prove VoxStride can hear you; this does.
    /// </summary>
    private async void TestMic_Click(object sender, RoutedEventArgs e)
    {
        if (_micTesting) return;
        _micTesting = true;
        TestSpeechButton.IsEnabled = false;
        var micName = MicBox.SelectedIndex <= 0 ? null : MicBox.SelectedItem as string;
        var shown = MicBox.SelectedItem as string ?? "microphone";
        try
        {
            MicTestText.Text = $"Listening on \"{shown}\" for 6 seconds — say something like \"open Notepad\"…";
            var r = await _app.Host!.TestSpeechAsync(micName, TimeSpan.FromSeconds(6),
                level => Dispatcher.BeginInvoke(() => MicLevel.Value = Math.Min(100, level / 60.0)),
                heard => Dispatcher.BeginInvoke(() => MicTestText.Text = $"Heard so far: \"{heard}\""),
                CancellationToken.None);
            MicTestText.Text = Describe(r, shown);
        }
        catch (Exception ex) { MicTestText.Text = "✖ " + ex.Message; Log.Error("speech", "speech test failed", ex); }
        finally
        {
            MicLevel.Value = 0;
            TestSpeechButton.IsEnabled = true;
            _micTesting = false;
        }
    }

    private static string Describe(AppHost.SpeechTestResult r, string shown)
    {
        if (r.Buffers == 0)
            return $"✖ \"{shown}\" didn't send any audio. Another app may be using it exclusively, or it's disconnected. Try another microphone above.";
        if (r.Peak < VoxStride.Core.Voice.MicHealthCheck.SilenceThreshold)
            return $"✖ No sound at all from \"{shown}\". Check it isn't muted, or pick another microphone above.";
        if (r.Heard.Length > 0)
            return $"✔ VoxStride heard: \"{r.Heard}\"  ({r.Engine}, {r.Device})." + (r.Engine.StartsWith("Windows offline", StringComparison.Ordinal)
                ? " If those aren't quite your words, the offline engine is struggling with your accent — cloud speech fixes that."
                : "");
        var quiet = r.Peak < 600 ? " The sound was very quiet — move closer or raise the microphone level in Windows Sound settings." : "";
        var problem = r.Problems.Count > 0 ? " (" + r.Problems[^1] + ")" : "";
        return $"⚠ The microphone works (level {r.Peak:0}) but the speech engine ({r.Engine}) didn't understand any words.{quiet}{problem}";
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DiagnosticsLog.Folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{DiagnosticsLog.Folder}\"") { UseShellExecute = true });
    }

    // ---------- Updates ----------
    private void RefreshUpdateUi()
    {
        VersionText.Text = $"VoxStride {UpdateService.CurrentVersion.ToString(3)}";
        if (_app.UpdateProgress is { } progress)
        {
            UpdateText.Text = progress;
            InstallUpdateButton.Visibility = Visibility.Visible;
            InstallUpdateButton.IsEnabled = progress.StartsWith("The update didn't", StringComparison.Ordinal);
            InstallUpdateButton.Content = InstallUpdateButton.IsEnabled ? "Try again" : "Updating…";
        }
        else if (_app.AvailableUpdate is { } u)
        {
            UpdateText.Text = $"Version {u.Version.ToString(3)} is available ({u.Size / 1_000_000} MB)." + (string.IsNullOrWhiteSpace(u.Notes) ? "" : " " + (u.Notes.Length > 300 ? u.Notes[..300] + "…" : u.Notes));
            InstallUpdateButton.Visibility = Visibility.Visible;
            InstallUpdateButton.IsEnabled = true;
            InstallUpdateButton.Content = "Install update now";
        }
        else
        {
            UpdateText.Text = _app.Settings.LastUpdateCheck == default ? "Updates come from the official GitHub releases and are checksum-verified before installing." : $"Last checked {_app.Settings.LastUpdateCheck.ToLocalTime():g}. You're up to date.";
            InstallUpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) { UpdateText.Text = "Checking…"; await _app.MaybeCheckForUpdatesAsync(true); RefreshUpdateUi(); }
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await _app.InstallUpdateAsync();

    private void UpdatePrefs_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.AutoCheckUpdates = AutoUpdateBox.IsChecked == true;
        _app.Settings.AutoInstallUpdates = AutoInstallBox.IsChecked == true;
        _app.Settings.Save();
    }

    // ---------- Privacy ----------
    private void RefreshExtensionStatus()
    {
        var ext = _app.Host!.Extension;
        ExtensionStatus.Text = ext.Connected ? $"● Connected ({ext.BrowserVersion})" : "○ Extension not connected";
    }

    private void SetupExtension_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "browser-extension");
        Log.Info("ui", "browser extension setup opened");
        if (Directory.Exists(folder))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        MessageBox.Show(this,
            "To connect VoxStride to your own browser:\n\n" +
            "1. In Edge open edge://extensions (in Chrome: chrome://extensions).\n" +
            "2. Turn on \"Developer mode\".\n" +
            "3. Click \"Load unpacked\" and choose the folder that just opened:\n   " + folder + "\n" +
            "4. Click the VoxStride button in the toolbar on a site and choose \"Allow on this site\".\n\n" +
            "VoxStride only works on sites you allow, only in the current tab, and never reads passwords or cookies.",
            "Set up the VoxStride browser extension", MessageBoxButton.OK, MessageBoxImage.Information);
        RefreshExtensionStatus();
    }

    private static List<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Allow VoxStride to use this folder", Multiselect = false };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FolderName;
        if (VoxStride.Core.Policy.PathPolicy.IsProtectedLocation(path) || path.Length <= 3)
        {
            _vm.AddActivity("That location can't be allowed (system folders and whole drives are always off-limits).", "!");
            return;
        }
        var list = ExtraFoldersList.Items.OfType<string>().ToList();
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
        ExtraFoldersList.ItemsSource = list;
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ExtraFoldersList.SelectedItem is not string sel) return;
        ExtraFoldersList.ItemsSource = ExtraFoldersList.Items.OfType<string>().Where(x => x != sel).ToList();
    }

    private async void SavePrivacy_Click(object sender, RoutedEventArgs e)
    {
        var s = _app.Settings;
        var micBefore = s.MicrophoneConsent;
        s.MicrophoneConsent = MicConsentBox.IsChecked == true;
        s.RetainTranscripts = RetainBox.IsChecked == true;
        s.LaunchAtLogon = StartupBox.IsChecked == true;
        s.LogConversationText = LogTextBox.IsChecked == true;
        Log.IncludeContent = s.LogConversationText;
        s.DisabledKnownFolders = FolderPermChecks.Children.OfType<CheckBox>().Where(c => c.IsChecked != true).Select(c => (string)c.Tag).ToList();
        s.AllowedFolders = ExtraFoldersList.Items.OfType<string>().ToList();
        s.BlockedApps = Lines(BlockedAppsBox.Text);
        s.BlockedSites = Lines(BlockedSitesBox.Text).Select(VoxStride.Core.Policy.UserPermissions.Normalize).Where(x => x.Length > 0).Distinct().ToList();
        if (ScreenPolicyBox.SelectedItem is ComboBoxItem { Tag: string sp }) s.ScreenCapture = Enum.Parse<VoxStride.Core.Policy.ScreenCapturePolicy>(sp);
        s.UseMyBrowser = UseMyBrowserBox.IsChecked == true;
        s.Save();
        _app.Host!.Store.RetainTranscripts = s.RetainTranscripts;
        _app.Host.ApplyStartup(s.LaunchAtLogon);
        if (micBefore && !s.MicrophoneConsent) { await _app.Host.StopWakeAsync(); _app.Host.Conversation.SetMuted(true); }
        if (!micBefore && s.MicrophoneConsent) { _app.Host.Conversation.SetMuted(false); await _app.Host.StartWakeAsync(); }
        _vm.AddActivity("Privacy settings saved.");
    }

    private async Task RefreshRulesAsync()
    {
        var rules = await _app.Host!.Store.GetRulesAsync(CancellationToken.None);
        RulesList.ItemsSource = rules.Select(r => new ListBoxItem { Content = $"Organize (reversible): {r.Scope}", Tag = r.RuleId }).ToList();
    }

    private async void RevokeRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesList.SelectedItem is ListBoxItem { Tag: string id }) { await _app.Host!.Store.RevokeRuleAsync(id, CancellationToken.None); await _app.Host.Rules.RefreshAsync(CancellationToken.None); await RefreshRulesAsync(); }
    }

    private async void RevokeAll_Click(object sender, RoutedEventArgs e)
    {
        await _app.Host!.Store.RevokeAllRulesAsync(CancellationToken.None);
        await _app.Host.Rules.RefreshAsync(CancellationToken.None);
        await RefreshRulesAsync();
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        await _app.Host!.Store.ClearHistoryAsync(false, CancellationToken.None);
        _vm.AddActivity("History cleared. Undo data for recent file moves was kept.");
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "This also removes undo information for past file moves. Continue?", "VoxStride", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _app.Host!.Store.ClearHistoryAsync(true, CancellationToken.None);
        _vm.AddActivity("History and undo data cleared.");
    }

    // ---------- Diagnostics ----------
    private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e) => RefreshDiagnostics();
    private void RefreshDiagnostics() => DiagnosticsText.Text = Diagnostics() + Environment.NewLine + DiagnosticsReport.Environment(_app.Settings, _app.Host) + Environment.NewLine + "Recent log:" + Environment.NewLine + RecentLog(60);

    private static string RecentLog(int lines)
    {
        try
        {
            DiagnosticsLog.Flush(TimeSpan.FromMilliseconds(500));
            using var fs = new FileStream(DiagnosticsLog.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            var all = r.ReadToEnd().Split('\n');
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception ex) { return "(log not readable: " + ex.Message + ")"; }
    }

    private string Diagnostics()
    {
        var h = _app.Host!;
        var p = System.Diagnostics.Process.GetCurrentProcess();
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"VoxStride {typeof(App).Assembly.GetName().Version}  ·  .NET {Environment.Version}  ·  {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Working set: {p.WorkingSet64 / 1048576.0:0.0} MB   Private: {p.PrivateMemorySize64 / 1048576.0:0.0} MB   Threads: {p.Threads.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Active provider: {_app.Settings.ActiveProvider?.ToString() ?? "none"}   Model: {(_app.Settings.ActiveProvider is { } k ? _app.Settings.Models.GetValueOrDefault(k) : "-")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Speech recognizer: {h.Recognizer.Name}   Cultures: {string.Join(", ", SpeechEnvironment.RecognizerCultures())}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Microphone: {h.MicrophoneLabel}   Devices: {string.Join(" | ", WaveInRecorder.Devices())}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Log: {DiagnosticsLog.FilePath}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Wake word: {(_app.Settings.WakeWordEnabled && _app.Settings.MicrophoneConsent ? "on" : "off")}   Mic consent: {_app.Settings.MicrophoneConsent}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"App catalog: {(h.Apps.IsReady ? h.Apps.Snapshot().Count + " apps" : "building")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Desktop: {h.FileServices.Folders.Resolve(KnownFolder.Desktop)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Tools: {string.Join(", ", h.Registry.Definitions.Select(d => d.Name))}");
        if (h.Runs.LastLatency is { } lat)
        {
            sb.AppendLine("Last run timings (ms from commit):");
            foreach (var m in new[] { "t_ui_ack", "t_route", "t_provider_request", "t_first_provider_delta", "t_first_useful_action", "t_first_verified_result", "t_verified" })
                if (lat.Between("t_commit", m) is { } v) sb.AppendLine(CultureInfo.InvariantCulture, $"  {m,-24} {v,8:0.0}");
        }
        return sb.ToString();
    }
}

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);
}
