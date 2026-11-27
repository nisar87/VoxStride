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
using VoxStride.Core.Policy;
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

    public IReasoningProvider? Current() => settings.ActiveProvider is { } kind ? Get(kind) : null;

    public IReasoningProvider? Fallback(ProviderKind failed)
    {
        if (settings.FallbackProvider is not { } kind || kind == failed) return null;
        var p = Get(kind);
        if (p is null) Log.Warn("ai", $"backup provider {kind} has no key; can't fail over");
        return p;
    }

    private IReasoningProvider? Get(ProviderKind kind)
    {
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
    public SwitchableRecognizer Recognizer { get; }
    private readonly bool _speechEnabled;
    /// <summary>The one owner of the microphone; wake word, dictation and tests all take leases from it.</summary>
    public AudioHub Microphone { get; }
    public NaturalSpeechSynthesizer? Speech { get; }
    public string SpeechEngineLabel { get; private set; } = "off";
    public string MicrophoneLabel { get; private set; } = "none";
    public WindowsWakeWordDetector? Wake { get; private set; }
    public PlaywrightBrowserController Browser { get; }
    public FileToolServices FileServices { get; }
    /// <summary>Folders the general file tools may use (Settings › Permissions).</summary>
    public FileScope Scope { get; }
    /// <summary>The user's blocked apps/sites and screen-capture choice (Settings › Permissions).</summary>
    public UserPermissions Permissions { get; }
    public WindowsUiAutomation UiAutomation { get; }
    /// <summary>The VoxStride extension's fixed id (from the public key in extension/manifest.json).</summary>
    public const string ExtensionId = "ljfgkidknpjooifilobgenemahnnefop";
    public ExtensionBrowser Extension { get; }
    public BrowserBridgeServer Bridge { get; }
    /// <summary>Browser tools' target: the user's browser via the extension when connected, else VoxStride's own window.</summary>
    public WebSession Web { get; }
    /// <summary>UI Automation tools' shared state (recent observations/screenshots).</summary>
    public UiSession Ui { get; }
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

        Permissions = new UserPermissions(() => settings.BlockedApps, () => settings.BlockedSites, () => settings.ScreenCapture);
        Extension = new ExtensionBrowser([ExtensionId], () => settings.UseMyBrowser);
        Bridge = new BrowserBridgeServer(Extension);
        Web = new WebSession(Browser, () => Extension, Permissions);
        UiAutomation = new WindowsUiAutomation();
        Ui = new UiSession(UiAutomation, Permissions);
        Scope = new FileScope(new WindowsKnownFolders(), () => settings.AllowedFolders,
            () => settings.DisabledKnownFolders.Select(n => Enum.TryParse<KnownFolder>(n, true, out var k) ? (KnownFolder?)k : null).OfType<KnownFolder>().ToList());
        Registry
            .Add(new ListFilesTool(Scope)).Add(new SearchFilesTool(Scope)).Add(new CreateFolderTool(Scope, Store))
            .Add(new MoveFilesTool(Scope, FileServices)).Add(new RenameFileTool(Scope, FileServices)).Add(new CopyFilesTool(Scope)).Add(new RecycleFilesTool(Scope, new WindowsFileRemover()))
            .Add(new GetFreeSpaceTool(drives)).Add(new ListDrivesTool(drives))
            .Add(new PreviewOrganizationTool(FileServices)).Add(new ApplyOrganizationTool(FileServices)).Add(new UndoOrganizationTool(FileServices))
            .Add(new PreviewTempCleanupTool(FileServices)).Add(new ApplyTempCleanupTool(FileServices))
            .Add(new LaunchAppTool(Apps, controller, Permissions)).Add(new ListAppsTool(Apps))
            .Add(new EditorWriteTool(new WindowsEditorAdapter(Apps), Apps))
            .Add(new ListWindowsTool(Ui)).Add(new FocusWindowTool(Ui)).Add(new ObserveUiTool(Ui)).Add(new InvokeUiTool(Ui))
            .Add(new SetValueUiTool(Ui)).Add(new TypeTextUiTool(Ui)).Add(new CaptureWindowTool(Ui)).Add(new ClickUiTool(Ui))
            .Add(new WebObserveTool(Web)).Add(new WebSearchTool(Web)).Add(new WebClickTool(Web)).Add(new WebFillTool(Web)).Add(new WebBackTool(Web))
            .Add(new BrowserOpenUrlTool(Browser, Permissions, Web)).Add(new YouTubePlayTool(Browser)).Add(new ChatGptDraftTool(Browser));

        Broker = new ToolBroker(Registry, Authority, Store, approvals, Rules, Events, time);
        Providers = new ProviderSelector(settings, Secrets, Store);

        if (enableSpeech)
        {
            try
            {
                var local = new WindowsSpeechSynthesizer(settings.Voice, settings.VoiceRate) { Enabled = settings.SpeakResults, CuesEnabled = settings.AudioCues };
                Speech = new NaturalSpeechSynthesizer(local, CreateCloudVoice());
            }
            catch (Exception) { Speech = null; } // no audio output device; text feedback still works
        }

        Runs = new RunCoordinator(new IntentRouter(Apps), Registry, Broker, Authority, Providers, Events, Speech, Store,
            new RunOptions { MaxSteps = settings.MaxSteps, MaxDuration = TimeSpan.FromSeconds(settings.MaxRunSeconds), MaxTokensPerRun = settings.MaxTokensPerRun, SpeakResults = settings.SpeakResults }, time);

        var control = new ControlPhraseDetector(new ControlPhraseOptions { AllowPlainProceed = settings.AllowPlainProceed });
        Microphone = new AudioHub(() => OpenMicrophone(Settings.MicrophoneName), m => Log.Info("mic", m));
        var label = "off";
        _speechEnabled = enableSpeech;
        Recognizer = new SwitchableRecognizer(enableSpeech ? CreateRecognizer(Microphone, out label) : new NullRecognizer(),
            r => r is CloudSpeechRecognizer ? TimeSpan.FromSeconds(8) : TimeSpan.FromMilliseconds(1500));
        SpeechEngineLabel = label;
        Recognizer.Hint += hint => Events.Publish(Guid.Empty, AgentState.Listening, "speech-hint", hint + ".", canCancel: true);
        Recognizer.Switched += name => Log.Info("speech", $"speech engine switched to {name}");
        Conversation = new ConversationCoordinator(Recognizer, Speech, Runs, control, new CoordinatorOptions
        {
            InactivityReminder = TimeSpan.FromSeconds(settings.InactivityReminderSeconds),
            InactivityDiscard = TimeSpan.FromSeconds(settings.InactivityDiscardSeconds),
            Language = settings.SpeechCulture,
            FinalizeFlushTimeout = TimeSpan.FromSeconds(8), // capped per engine by the switchable recognizer
            SelectedProvider = () => settings.ActiveProvider?.ToString(),
            NaturalTurnPause = settings.NaturalMode ? TimeSpan.FromSeconds(Math.Clamp(settings.NaturalPauseSeconds, 0.8, 5)) : null,
        }, time);
    }

    /// <summary>Opens the chosen microphone, falling back to the Windows default if it's no longer connected.</summary>
    private static WaveInRecorder OpenMicrophone(string? name)
    {
        var id = WaveInRecorder.ResolveDevice(name);
        if (id < 0 && !string.IsNullOrWhiteSpace(name)) Log.Warn("mic", $"saved microphone \"{name}\" is not connected; using the Windows default");
        return new WaveInRecorder(id);
    }

    public static readonly string[] ControlPhrases = ["VoxStride execute", "VoxStride proceed", "assistant proceed", "execute now", "cancel", "never mind", "scratch that", "undo last sentence", "stop listening"];

    /// <summary>The cloud transcriber the settings allow, or null (needs explicit consent and an OpenAI or Gemini key).</summary>
    private ITranscriber? CloudTranscriber()
    {
        if (!Settings.CloudSpeechConsent || Settings.SpeechEngine == SpeechEngine.Windows) return null;
        var openAiKey = Secrets.Load(ProviderSelector.SecretName(ProviderKind.OpenAI));
        var geminiKey = Secrets.Load(ProviderSelector.SecretName(ProviderKind.Gemini));
        var geminiModel = Settings.Models.GetValueOrDefault(ProviderKind.Gemini, ProviderFactory.DefaultModel(ProviderKind.Gemini));
        return Settings.SpeechEngine switch
        {
            SpeechEngine.OpenAI when !string.IsNullOrEmpty(openAiKey) => new OpenAITranscriber(openAiKey),
            SpeechEngine.Gemini when !string.IsNullOrEmpty(geminiKey) => new GeminiTranscriber(geminiKey, geminiModel),
            SpeechEngine.Auto or SpeechEngine.OpenAI or SpeechEngine.Gemini when !string.IsNullOrEmpty(openAiKey) => new OpenAITranscriber(openAiKey),
            SpeechEngine.Auto or SpeechEngine.OpenAI or SpeechEngine.Gemini when !string.IsNullOrEmpty(geminiKey) => new GeminiTranscriber(geminiKey, geminiModel),
            _ => null,
        };
    }

    /// <summary>True when only the basic offline engine is available (no cloud key/consent).</summary>
    public bool UsingBasicEngine => Recognizer.Current is WindowsSpeechRecognizer;

    /// <summary>
    /// Builds the speech recognizer on the given microphone: cloud speech (accurate with accents) when allowed,
    /// otherwise the offline Windows engine. Used for both live listening and the speech test, so the test
    /// exercises exactly the same pipeline.
    /// </summary>
    private ISpeechRecognizer CreateRecognizer(AudioHub microphone, out string label)
    {
        if (CloudTranscriber() is { } cloud)
        {
            label = cloud.Name;
            return new CloudSpeechRecognizer(cloud, microphone, Settings.MicSensitivity);
        }
        label = "Windows offline (" + (SpeechEnvironment.PickRecognizer(Settings.SpeechCulture)?.Culture.Name ?? Settings.SpeechCulture) + ")";
        return new WindowsSpeechRecognizer(Settings.SpeechCulture, ControlPhrases, microphone);
    }

    /// <summary>
    /// Re-picks the speech engine after a key or voice setting changed (e.g. a Gemini key was saved), without a
    /// restart. If an instruction is being dictated, the new engine takes over when that listening session ends.
    /// </summary>
    public async Task RefreshSpeechEngineAsync(bool force = false)
    {
        if (!_speechEnabled) return;
        var next = CreateRecognizer(Microphone, out var label);
        if (!force && label == SpeechEngineLabel) { await next.DisposeAsync(); return; }
        Log.Info("speech", $"speech engine change requested: {SpeechEngineLabel} -> {label}");
        SpeechEngineLabel = label;
        await Recognizer.ReplaceAsync(next);
    }

    public sealed record SpeechTestResult(string Engine, string Device, long Buffers, double Peak, string Heard, IReadOnlyList<string> Problems);

    /// <summary>
    /// End-to-end check: listens on the given microphone with the real recognizer for a few seconds and returns the
    /// words it understood, plus what the microphone delivered. Only runs while no instruction is in progress.
    /// </summary>
    public async Task<SpeechTestResult> TestSpeechAsync(string? microphoneName, TimeSpan duration, Action<double> level, Action<string> heard, CancellationToken ct)
    {
        if (!_conversationIdle) throw new InvalidOperationException("Finish or cancel the current instruction first.");
        await StopWakeAsync();
        var problems = new List<string>();
        var text = new System.Text.StringBuilder();
        try
        {
            using var hub = new AudioHub(() => OpenMicrophone(microphoneName), m => Log.Info("mic", "[test] " + m));
            hub.Level += level;
            using var meter = hub.Acquire("speech-test");
            var rec = CreateRecognizer(hub, out var engine);
            rec.Error += problems.Add;
            if (rec is ISpeechDiagnostics d) d.Hint += problems.Add;
            rec.Transcript += e =>
            {
                if (!e.IsFinal || string.IsNullOrWhiteSpace(e.Text)) return;
                lock (text) { text.Append(text.Length > 0 ? " " : "").Append(e.Text.Trim()); heard(text.ToString()); }
            };
            try
            {
                await rec.StartAsync(Guid.NewGuid(), ct);
                await Task.Delay(duration, ct);
                await rec.StopAsync(rec is CloudSpeechRecognizer ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(2), ct);
            }
            finally { await rec.DisposeAsync(); }
            string result;
            lock (text) result = text.ToString();
            Log.Info("speech", $"speech test engine={engine} device=\"{meter.DeviceLabel}\" buffers={meter.Buffers} peak={meter.Peak:0} words={result.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} problems={problems.Count} heard={Log.Text(result)}" + (problems.Count > 0 ? " lastProblem=" + problems[^1] : ""));
            return new SpeechTestResult(engine, meter.DeviceLabel, meter.Buffers, meter.Peak, result, problems);
        }
        finally { _ = StartWakeAsync(); }
    }

    /// <summary>Setup check: listens for "Hey VoxStride" for a few seconds on the chosen microphone; returns whether it was heard.</summary>
    public async Task<bool> TestWakeAsync(TimeSpan timeout, CancellationToken ct)
    {
        await StopWakeAsync();
        var heard = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var w = new WindowsWakeWordDetector(Settings.SpeechCulture, Microphone);
        w.WakeDetected += _ => heard.TrySetResult(true);
        try
        {
            await w.StartAsync(ct);
            var done = await Task.WhenAny(heard.Task, Task.Delay(timeout, ct));
            var ok = done == heard.Task;
            Log.Info("wake", $"wake test {(ok ? "heard the wake phrase" : "timed out")}");
            return ok;
        }
        finally
        {
            await w.StopAsync();
            await w.DisposeAsync();
            if (_conversationIdle) _ = StartWakeAsync();
        }
    }

    /// <summary>Applies a newly chosen microphone without a restart.</summary>
    public void MicrophoneChanged()
    {
        Log.Info("mic", $"microphone selected \"{Settings.MicrophoneName ?? "Windows default"}\" (device open={Microphone.IsOpen}, leases={Microphone.LeaseCount})");
        MicrophoneLabel = WaveInRecorder.DescribeDevice(WaveInRecorder.ResolveDevice(Settings.MicrophoneName));
        try { Microphone.Reopen(); } catch (Exception ex) { Log.Error("mic", "re-opening the microphone failed", ex); }
    }

    private ICloudVoice? CreateCloudVoice()
    {
        if (!Settings.NaturalVoice) return null;
        var key = Secrets.Load(ProviderSelector.SecretName(ProviderKind.OpenAI));
        return string.IsNullOrEmpty(key) ? null : new OpenAIVoice(key, Settings.CloudVoiceName);
    }

    public void RefreshVoice() { if (Speech is not null) Speech.Cloud = CreateCloudVoice(); }

    public async Task StartAsync(CancellationToken ct)
    {
        MicrophoneLabel = WaveInRecorder.DescribeDevice(WaveInRecorder.ResolveDevice(Settings.MicrophoneName));
        Log.Info("app", $"host start v{UpdateService.CurrentVersion.ToString(3)} speech={SpeechEngineLabel} mic={MicrophoneLabel} micDevices=[{string.Join(" | ", WaveInRecorder.Devices())}] provider={Settings.ActiveProvider?.ToString() ?? "none"} cloudSpeechConsent={Settings.CloudSpeechConsent}");
        _ = Apps.BuildAsync(ct); // catalog builds in the background; direct disk queries never wait for it
        await Rules.RefreshAsync(ct);
        var unresolved = await Store.GetUnresolvedMovesAsync(ct);
        if (unresolved.Count > 0)
            Events.Publish(Guid.Empty, AgentState.Standby, "recovery", $"{unresolved.Count} file move(s) from an interrupted task could not be confirmed. They were not repeated. See History.", canCancel: false);
        Bridge.Start();
        Log.Info("http", $"browser bridge listening on pipe {Bridge.PipeName} for extension {ExtensionId}");
        StartMicWatch();
        await StartWakeAsync();
    }

    private Timer? _micWatch;
    private string? _yieldedTo;
    private bool _conversationIdle = true;

    /// <summary>
    /// Checks every 3 s whether another app is using the microphone. If so, VoxStride stops its background
    /// "Hey VoxStride" listening so calls in Teams/Zoom are never affected, and resumes when the mic is free.
    /// </summary>
    private void StartMicWatch() => _micWatch = new Timer(async _ =>
    {
        if (!Settings.YieldMicToOtherApps) return;
        var other = MicrophoneUsage.OtherAppInUse();
        if (other is not null && _yieldedTo is null)
        {
            _yieldedTo = other;
            await StopWakeAsync();
            Log.Info("mic", $"yielding: {other} is using the microphone; wake word paused");
            Events.Publish(Guid.Empty, AgentState.Standby, "wake-unavailable", $"{other} is using the microphone, so I've stopped listening for \"Hey VoxStride\" until it's done. Ctrl+Alt+V still works.", canCancel: false);
        }
        else if (other is null && _yieldedTo is not null)
        {
            Log.Info("mic", "microphone free again; wake word resumes" + (_conversationIdle ? "" : " after the current instruction"));
            _yieldedTo = null;
            if (_conversationIdle) await StartWakeAsync();
        }
    }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));

    public void SetConversationIdle(bool idle) => _conversationIdle = idle;

    public async Task StartWakeAsync()
    {
        if (!Settings.WakeWordEnabled || !Settings.MicrophoneConsent || Recognizer.Current is NullRecognizer) { Log.Debug("wake", $"wake not started (enabled={Settings.WakeWordEnabled}, micConsent={Settings.MicrophoneConsent}, speech={Recognizer.Current is not NullRecognizer})"); return; }
        if (_yieldedTo is not null) { Log.Debug("wake", $"wake not started: {_yieldedTo} is using the microphone"); return; } // another app is using the microphone
        try
        {
            Wake ??= CreateWake();
            await Wake.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Events.Publish(Guid.Empty, AgentState.Standby, "wake-unavailable", "Wake word is unavailable (" + ex.Message + "). Use Ctrl+Alt+V instead.", canCancel: false);
            Log.Error("wake", "wake word could not start", ex);
        }
    }

    private WindowsWakeWordDetector CreateWake()
    {
        var w = new WindowsWakeWordDetector(Settings.SpeechCulture, Microphone);
        string? trailing = null;
        w.TrailingText += t => trailing = t;
        w.WakeDetected += detectedAt =>
        {
            // VoxStride's own voice (or a cue) must never wake it.
            if (Speech is { } sp && (sp.IsSpeaking || DateTimeOffset.UtcNow - sp.LastSpeechEnded < TimeSpan.FromMilliseconds(700)))
            {
                Log.Info("wake", "wake phrase ignored: VoxStride itself was speaking");
                return;
            }
            var handover = Microphone.Position;
            // TrailingText is raised right after WakeDetected for the same utterance; defer one tick to collect it.
            _ = Task.Run(async () =>
            {
                await Task.Delay(30);
                var text = Interlocked.Exchange(ref trailing, null);
                // Hold the microphone open across the handover so dictation starts on the same open device
                // instead of closing and reopening it (which loses the first words).
                Log.Info("wake", "wake phrase detected; handing the microphone to dictation" + (text is null ? "" : " trailing=" + Log.Text(text)));
                var bridge = Microphone.Acquire("handoff");
                Microphone.ArmReplay(handover, TimeSpan.FromSeconds(3)); // words spoken during the hand-over are replayed into dictation
                await w.StopAsync();
                Conversation.ActivateFromWake(text);
                await Task.Delay(2000);
                bridge.Dispose();
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
        _micWatch?.Dispose();
        await Runs.CancelActiveAsync("shutdown");
        await Conversation.DisposeAsync();
        if (Wake is not null) await Wake.DisposeAsync();
        await Recognizer.DisposeAsync();
        Microphone.Dispose();
        await Browser.DisposeAsync();
        UiAutomation.Dispose();
        Bridge.Dispose();
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
