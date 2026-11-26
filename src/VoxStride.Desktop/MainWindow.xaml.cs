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
        WelcomePanel.Visibility = app.Settings.FirstRunCompleted ? Visibility.Collapsed : Visibility.Visible;
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

    // ---------- Home ----------
    private void Welcome_Continue(object sender, RoutedEventArgs e) => CompleteWelcome(ConsentBox.IsChecked == true);
    private void Welcome_TypedOnly(object sender, RoutedEventArgs e) => CompleteWelcome(false);

    private void CompleteWelcome(bool mic)
    {
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

    private void Talk_Click(object sender, RoutedEventArgs e) => _app.TalkOrProceed();
    private void Stop_Click(object sender, RoutedEventArgs e) => _app.Host?.Conversation.PressCancel();
    private void Proceed_Click(object sender, RoutedEventArgs e) => _app.Host?.Conversation.PressProceed();

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

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var items = await _app.Host!.Store.GetHistoryAsync(200, CancellationToken.None);
        var redacted = items.Select(h => new
        {
            h.RunId, h.StartedAt, h.Route, h.Provider, h.Success, h.InputTokens, h.OutputTokens,
            Steps = h.Steps.Select(s => new { s.Tool, Outcome = s.Outcome.ToString(), s.Verified, s.DurationMs }),
        });
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"VoxStride-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Diagnostics = Diagnostics(), Runs = redacted }, new JsonSerializerOptions { WriteIndented = true }));
        _vm.AddActivity("Exported redacted diagnostics (no instructions, paths, keys or audio) to " + path);
    }

    // ---------- Providers ----------
    private void LoadSettingsIntoUi()
    {
        var s = _app.Settings;
        foreach (ComboBoxItem item in ActiveProviderBox.Items)
            if ((string)item.Tag == (s.ActiveProvider?.ToString() ?? "")) ActiveProviderBox.SelectedItem = item;
        BuildProviderCards();
        DailyBudgetBox.Text = s.DailyTokenBudget.ToString(CultureInfo.InvariantCulture);
        RunBudgetBox.Text = s.MaxTokensPerRun.ToString(CultureInfo.InvariantCulture);
        MaxStepsBox.Text = s.MaxSteps.ToString(CultureInfo.InvariantCulture);

        WakeBox.IsChecked = s.WakeWordEnabled; PlainProceedBox.IsChecked = s.AllowPlainProceed; SpeakBox.IsChecked = s.SpeakResults;
        CuesBox.IsChecked = s.AudioCues; OverlayBox.IsChecked = s.ShowOverlay;
        var cultures = SpeechEnvironment.RecognizerCultures();
        CultureBox.ItemsSource = cultures.Count > 0 ? cultures : new List<string> { s.SpeechCulture };
        CultureBox.SelectedItem = cultures.Contains(s.SpeechCulture) ? s.SpeechCulture : cultures.FirstOrDefault();
        var voices = SpeechEnvironment.Voices();
        VoiceBox.ItemsSource = voices;
        VoiceBox.SelectedItem = s.Voice is not null && voices.Contains(s.Voice) ? s.Voice : voices.FirstOrDefault();
        VoiceInfo.Text = cultures.Count == 0
            ? "No Windows speech recognizer is installed. Install an English speech pack (Settings › Time & language › Speech) to use voice. Typed commands still work."
            : $"Installed recognizers: {string.Join(", ", cultures)}. Voices: {voices.Count}.";

        CloudSpeechBox.IsChecked = s.CloudSpeechConsent;
        foreach (ComboBoxItem item in EngineBox.Items) if ((string)item.Tag == s.SpeechEngine.ToString()) EngineBox.SelectedItem = item;
        SensitivitySlider.Value = s.MicSensitivity;
        NaturalVoiceBox.IsChecked = s.NaturalVoice;
        CloudVoiceBox.ItemsSource = OpenAIVoice.Voices;
        CloudVoiceBox.SelectedItem = s.CloudVoiceName;
        var hasOpenAi = _app.Host!.Secrets.Load(ProviderSelector.SecretName(ProviderKind.OpenAI)) is { Length: > 0 };
        var hasGemini = _app.Host.Secrets.Load(ProviderSelector.SecretName(ProviderKind.Gemini)) is { Length: > 0 };
        EngineInfo.Text = $"Currently using: {_app.Host.SpeechEngineLabel}." + (!hasOpenAi && !hasGemini ? " Add an OpenAI or Gemini key under Reasoning providers to enable cloud speech." : "");
        AutoUpdateBox.IsChecked = s.AutoCheckUpdates;
        AutoInstallBox.IsChecked = s.AutoInstallUpdates;
        RefreshUpdateUi();
        MicConsentBox.IsChecked = s.MicrophoneConsent; RetainBox.IsChecked = s.RetainTranscripts; StartupBox.IsChecked = s.LaunchAtLogon;
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
                    try { _app.Host!.Secrets.Save(ProviderSelector.SecretName(kind), key.Password.Trim()); key.Clear(); status.Text = "Key saved in Credential Manager"; }
                    catch (InvalidOperationException ex) { status.Text = ex.Message; }
                }
                if (_app.Settings.ActiveProvider is null && _app.Host!.Secrets.Load(ProviderSelector.SecretName(kind)) is { Length: > 0 }) _app.Settings.ActiveProvider = kind;
                _app.Settings.Save();
                _app.Host!.Providers.Invalidate();
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
                if (_app.Settings.ActiveProvider == kind) _app.Settings.ActiveProvider = null;
                _app.Settings.Save();
                _app.Host!.Providers.Invalidate();
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
        s.WakeWordEnabled = WakeBox.IsChecked == true; s.AllowPlainProceed = PlainProceedBox.IsChecked == true;
        s.SpeakResults = SpeakBox.IsChecked == true; s.AudioCues = CuesBox.IsChecked == true; s.ShowOverlay = OverlayBox.IsChecked == true;
        if (CultureBox.SelectedItem is string c) s.SpeechCulture = c;
        if (VoiceBox.SelectedItem is string v) s.Voice = v;
        var engineBefore = (s.CloudSpeechConsent, s.SpeechEngine, s.MicSensitivity);
        s.CloudSpeechConsent = CloudSpeechBox.IsChecked == true;
        if (EngineBox.SelectedItem is ComboBoxItem { Tag: string tag }) s.SpeechEngine = Enum.Parse<SpeechEngine>(tag);
        s.MicSensitivity = SensitivitySlider.Value;
        s.NaturalVoice = NaturalVoiceBox.IsChecked == true;
        if (CloudVoiceBox.SelectedItem is string cv) s.CloudVoiceName = cv;
        s.Save();
        _app.Host!.RefreshVoice();
        if (engineBefore != (s.CloudSpeechConsent, s.SpeechEngine, s.MicSensitivity))
        {
            if (MessageBox.Show(this, "The new speech settings take effect after VoxStride restarts. Restart now?", "VoxStride", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            { _app.RestartApp(); return; }
        }
        if (_app.Host!.Speech is { } sp) { sp.Enabled = s.SpeakResults; sp.CuesEnabled = s.AudioCues; }
        if (wakeChanged) { if (s.WakeWordEnabled) await _app.Host.StartWakeAsync(); else await _app.Host.StopWakeAsync(); }
        _app.ApplyOverlayVisibility();
        _vm.AddActivity("Voice settings saved. Language, voice and finish-phrase changes apply after restart.");
    }

    private void TestVoice_Click(object sender, RoutedEventArgs e) =>
        _ = _app.Host!.Speech?.SpeakAsync("Hi! I'm VoxStride. Just tell me what you need, take your time, and say VoxStride, execute when you're done.", CancellationToken.None);

    private void Restart_Click(object sender, RoutedEventArgs e) => _app.RestartApp();

    // ---------- Updates ----------
    private void RefreshUpdateUi()
    {
        VersionText.Text = $"VoxStride {UpdateService.CurrentVersion.ToString(3)}";
        if (_app.AvailableUpdate is { } u)
        {
            UpdateText.Text = $"Version {u.Version.ToString(3)} is available." + (string.IsNullOrWhiteSpace(u.Notes) ? "" : " " + (u.Notes.Length > 300 ? u.Notes[..300] + "…" : u.Notes));
            InstallUpdateButton.Visibility = Visibility.Visible;
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
    private async void SavePrivacy_Click(object sender, RoutedEventArgs e)
    {
        var s = _app.Settings;
        var micBefore = s.MicrophoneConsent;
        s.MicrophoneConsent = MicConsentBox.IsChecked == true;
        s.RetainTranscripts = RetainBox.IsChecked == true;
        s.LaunchAtLogon = StartupBox.IsChecked == true;
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
    private void RefreshDiagnostics() => DiagnosticsText.Text = Diagnostics();

    private string Diagnostics()
    {
        var h = _app.Host!;
        var p = System.Diagnostics.Process.GetCurrentProcess();
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"VoxStride {typeof(App).Assembly.GetName().Version}  ·  .NET {Environment.Version}  ·  {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Working set: {p.WorkingSet64 / 1048576.0:0.0} MB   Private: {p.PrivateMemorySize64 / 1048576.0:0.0} MB   Threads: {p.Threads.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Active provider: {_app.Settings.ActiveProvider?.ToString() ?? "none"}   Model: {(_app.Settings.ActiveProvider is { } k ? _app.Settings.Models.GetValueOrDefault(k) : "-")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Speech recognizer: {h.Recognizer.Name}   Cultures: {string.Join(", ", SpeechEnvironment.RecognizerCultures())}");
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
