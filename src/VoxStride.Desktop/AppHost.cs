using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using VoxStride.Browser;
using VoxStride.Contracts;
using VoxStride.Core.Agent;
using VoxStride.Core.Events;
using VoxStride.Core.Execution;
using VoxStride.Core.Files;
using VoxStride.Core.Routing;
using VoxStride.Core.Tools;
using VoxStride.Core.Voice;
using VoxStride.Persistence;
using VoxStride.Providers;
using VoxStride.Speech;
using VoxStride.Windows;

namespace VoxStride.Desktop;

/// <summary>Selects the configured provider at command boundary; enforces the daily token budget locally.</summary>
public sealed class ProviderSelector(AppSettings settings, CredentialSecretStore secrets, SqliteStore store) : IProviderSelector
{
    private readonly Dictionary<(ProviderKind, string), IReasoningProvider> _cache = new();
    private readonly Lock _gate = new();

    public static string SecretName(ProviderKind k) => "provider/" + k.ToString().ToLowerInvariant();

    public IReasoningProvider? Current()
    {
        if (settings.ActiveProvider is not { } kind) return null;
        var key = secrets.Load(SecretName(kind));
        if (string.IsNullOrWhiteSpace(key)) return null;
        var since = DateTimeOffset.UtcNow.Date;
        var (i, o) = store.GetUsageSinceAsync(new DateTimeOffset(since, TimeSpan.Zero), kind.ToString(), CancellationToken.None).GetAwaiter().GetResult();
        if (settings.DailyTokenBudget > 0 && i + o >= settings.DailyTokenBudget) return new BudgetExhaustedProvider(kind);
        var model = settings.Models.TryGetValue(kind, out var m) && !string.IsNullOrWhiteSpace(m) ? m : ProviderFactory.DefaultModel(kind);
        lock (_gate)
        {
            if (!_cache.TryGetValue((kind, model), out var p) || KeyChanged(p, key))
            {
                p = ProviderFactory.Create(new ProviderSettings(kind, model, key));
                _cache[(kind, model)] = p;
                _keys[(kind, model)] = key.GetHashCode(StringComparison.Ordinal);
            }
            return p;
        }
    }

    private readonly Dictionary<(ProviderKind, string), int> _keys = new();
    private bool KeyChanged(IReasoningProvider p, string key) =>
        !_keys.TryGetValue((p.Kind, p.Model), out var h) || h != key.GetHashCode(StringComparison.Ordinal);

    public void Invalidate() { lock (_gate) { _cache.Clear(); _keys.Clear(); } }

    private sealed class BudgetExhaustedProvider(ProviderKind kind) : IReasoningProvider
    {
        public ProviderKind Kind => kind;
        public string Model => "budget";
        public ProviderCapabilities Capabilities => new(false, false, false, 0);
        public IProviderConversation StartConversation(string systemInstruction, IReadOnlyList<ToolDefinition> tools) => new Conv();
        public Task<ProviderCheckResult> CheckAsync(CancellationToken ct) => Task.FromResult(new ProviderCheckResult(false, "Daily budget reached."));
        private sealed class Conv : IProviderConversation
        {
            public void AddUserText(string text) { }
            public void AddToolResults(IReadOnlyList<ToolCallOutput> outputs) { }
            public async IAsyncEnumerable<ProviderEvent> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await Task.CompletedTask;
                yield return new ProviderErrorEvent(ProviderErrorKind.BudgetExceeded, "Your daily usage limit for this provider has been reached. Raise it in Settings.");
            }
        }
    }
}

/// <summary>Composition root: wires policy, broker, journal, tools, providers, speech and coordinators.</summary>
public sealed class AppHost : IAsyncDisposable
{
    public AppSettings Settings { get; }
    public SqliteStore Store { get; }
    public StoredStandingRules Rules { get; }
    public CredentialSecretStore Secrets { get; } = new();
    public EventHub Events { get; } = new();
    public WindowsAppCatalog Apps { get; } = new();
    public RunAuthority Authority { get; } = new(TimeProvider.System);
    public ToolRegistry Registry { get; } = new();
    public ToolBroker Broker { get; }
    public ProviderSelector Providers { get; }
    public RunCoordinator Runs { get; }
    public ConversationCoordinator Conversation { get; }
    public ISpeechRecognizer Recognizer { get; }
    public WindowsSpeechSynthesizer? Speech { get; }
    public WindowsWakeWordDetector? Wake { get; private set; }
    public PlaywrightBrowserController Browser { get; }
    public FileToolServices FileServices { get; }
    public IUserApproval Approvals { get; }

    public AppHost(AppSettings settings, IUserApproval approvals, bool enableSpeech)
    {
        Settings = settings;
        Approvals = approvals;
        Store = new SqliteStore(SqliteStore.DefaultPath()) { RetainTranscripts = settings.RetainTranscripts };
        Rules = new StoredStandingRules(Store);
        var time = TimeProvider.System;
        var drives = new SystemDriveInfoSource();
        FileServices = new FileToolServices(new WindowsKnownFolders(), new ManifestStore(time), new MoveExecutor(Store, time), new OrganizationPlanner(time), new TempCleaner(time), new WindowsFileRemover(), drives);
        Browser = new PlaywrightBrowserController();
        var controller = new WindowsAppController();

        Registry
            .Add(new GetFreeSpaceTool(drives)).Add(new ListDrivesTool(drives))
            .Add(new PreviewOrganizationTool(FileServices)).Add(new ApplyOrganizationTool(FileServices)).Add(new UndoOrganizationTool(FileServices))
            .Add(new PreviewTempCleanupTool(FileServices)).Add(new ApplyTempCleanupTool(FileServices))
            .Add(new LaunchAppTool(Apps, controller)).Add(new ListAppsTool(Apps))
            .Add(new EditorWriteTool(new WindowsEditorAdapter(Apps), Apps))
            .Add(new BrowserOpenUrlTool(Browser)).Add(new YouTubePlayTool(Browser)).Add(new ChatGptDraftTool(Browser));

        Broker = new ToolBroker(Registry, Authority, Store, approvals, Rules, Events, time);
        Providers = new ProviderSelector(settings, Secrets, Store);

        if (enableSpeech)
        {
            try { Speech = new WindowsSpeechSynthesizer(settings.Voice, settings.VoiceRate) { Enabled = settings.SpeakResults, CuesEnabled = settings.AudioCues }; }
            catch (Exception) { Speech = null; } // no audio output device; text feedback still works
        }

        Runs = new RunCoordinator(new IntentRouter(Apps), Registry, Broker, Authority, Providers, Events, Speech, Store,
            new RunOptions { MaxSteps = settings.MaxSteps, MaxDuration = TimeSpan.FromSeconds(settings.MaxRunSeconds), MaxTokensPerRun = settings.MaxTokensPerRun, SpeakResults = settings.SpeakResults }, time);

        var control = new ControlPhraseDetector(new ControlPhraseOptions { AllowPlainProceed = settings.AllowPlainProceed });
        Recognizer = enableSpeech
            ? new WindowsSpeechRecognizer(settings.SpeechCulture, ["VoxStride execute", "VoxStride proceed", "assistant proceed", "execute now", "cancel", "never mind", "scratch that", "undo last sentence", "stop listening"])
            : new NullRecognizer();
        Conversation = new ConversationCoordinator(Recognizer, Speech, Runs, control, new CoordinatorOptions
        {
            InactivityReminder = TimeSpan.FromSeconds(settings.InactivityReminderSeconds),
            InactivityDiscard = TimeSpan.FromSeconds(settings.InactivityDiscardSeconds),
            Language = settings.SpeechCulture,
            SelectedProvider = () => settings.ActiveProvider?.ToString(),
        }, time);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _ = Apps.BuildAsync(ct); // catalog builds in the background; direct disk queries never wait for it
        await Rules.RefreshAsync(ct);
        var unresolved = await Store.GetUnresolvedMovesAsync(ct);
        if (unresolved.Count > 0)
            Events.Publish(Guid.Empty, AgentState.Standby, "recovery", $"{unresolved.Count} file move(s) from an interrupted task could not be confirmed. They were not repeated. See History.", canCancel: false);
        await StartWakeAsync();
    }

    public async Task StartWakeAsync()
    {
        if (!Settings.WakeWordEnabled || !Settings.MicrophoneConsent || Recognizer is NullRecognizer) return;
        try
        {
            Wake ??= CreateWake();
            await Wake.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Events.Publish(Guid.Empty, AgentState.Standby, "wake-unavailable", "Wake word is unavailable (" + ex.Message + "). Use Ctrl+Alt+V instead.", canCancel: false);
        }
    }

    private WindowsWakeWordDetector CreateWake()
    {
        var w = new WindowsWakeWordDetector(Settings.SpeechCulture);
        string? trailing = null;
        w.TrailingText += t => trailing = t;
        w.WakeDetected += _ =>
        {
            // TrailingText is raised right after WakeDetected for the same utterance; defer one tick to collect it.
            _ = Task.Run(async () =>
            {
                await Task.Delay(30);
                var text = Interlocked.Exchange(ref trailing, null);
                await w.StopAsync(); // free the mic for dictation
                Conversation.ActivateFromWake(text);
            });
        };
        return w;
    }

    public Task StopWakeAsync() => Wake?.StopAsync() ?? Task.CompletedTask;

    public void ApplyStartup(bool enabled)
    {
        using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (run is null) return;
        if (enabled) run.SetValue("VoxStride", $"\"{Environment.ProcessPath}\" --background");
        else run.DeleteValue("VoxStride", throwOnMissingValue: false);
    }

    public async ValueTask DisposeAsync()
    {
        await Runs.CancelActiveAsync("shutdown");
        await Conversation.DisposeAsync();
        if (Wake is not null) await Wake.DisposeAsync();
        await Recognizer.DisposeAsync();
        await Browser.DisposeAsync();
        Speech?.Dispose();
        Store.Dispose();
    }
}

/// <summary>Used when speech is disabled (typed-only mode, smoke tests).</summary>
public sealed class NullRecognizer : ISpeechRecognizer
{
    public string Name => "none";
    public event Action<TranscriptEvent>? Transcript { add { } remove { } }
    public event Action<string>? Error;
    public Task StartAsync(Guid sessionId, CancellationToken ct) { Error?.Invoke("Speech input is turned off. Type your instruction instead."); return Task.CompletedTask; }
    public Task StopAsync(TimeSpan flushTimeout, CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
