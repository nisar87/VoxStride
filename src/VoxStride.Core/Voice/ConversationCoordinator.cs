using System.Threading.Channels;
using VoxStride.Contracts;
using VoxStride.Core.Events;
using VoxStride.Core.Execution;

namespace VoxStride.Core.Voice;

/// <summary>Receives committed commands. Implemented by the run coordinator.</summary>
public interface ICommandSink
{
    Task<RunSummary> RunAsync(CommandEnvelope command, CancellationToken ct);
    Task CancelActiveAsync(string reason);
}

public sealed record RunSummary(bool Success, string SpokenSummary);

public sealed class CoordinatorOptions
{
    public TimeSpan InactivityReminder { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan InactivityDiscard { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan FinalizeFlushTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan FollowUpWindow { get; init; } = TimeSpan.FromSeconds(90);
    public string Language { get; init; } = "en-US";
    public Func<string?> SelectedProvider { get; init; } = () => null;
    /// <summary>
    /// Opt-in natural mode: a pause this long after the last final words ends the turn. Such a turn is committed as
    /// <see cref="InputMode.VoiceNatural"/>, which may only read and answer — any change to the PC still needs
    /// "VoxStride, execute" or Proceed. Null (default) = off: only an explicit finish commits.
    /// </summary>
    public TimeSpan? NaturalTurnPause { get; init; }
}

/// <summary>
/// Single-reader actor that owns the voice/command lifecycle (spec §14.2). It is the only component that can
/// create a <see cref="CommandEnvelope"/>. Silence, timeouts, reconnects and ambient audio never commit.
/// </summary>
public sealed class ConversationCoordinator : IAsyncDisposable
{
    private abstract record Msg;
    private sealed record Activate(string Source, string? TrailingText = null) : Msg;
    private sealed record Transcript(TranscriptEvent Event) : Msg;
    private sealed record Proceed : Msg;
    private sealed record CancelPressed : Msg;
    private sealed record Mute(bool On) : Msg;
    private sealed record Locked(bool IsLocked) : Msg;
    private sealed record Tick : Msg;
    private sealed record Typed(string Text) : Msg;
    private sealed record Edit(string Text) : Msg;
    private sealed record FinalizeDone(long Generation) : Msg;
    private sealed record RunDone(long Generation, RunSummary Summary) : Msg;
    private sealed record RecognizerFailed(string Message) : Msg;
    private sealed record Drain(TaskCompletionSource Tcs) : Msg;

    private readonly Channel<Msg> _inbox = Channel.CreateUnbounded<Msg>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ISpeechRecognizer _recognizer;
    private readonly ISpeechSynthesizer? _speech;
    private readonly ICommandSink _sink;
    private readonly ControlPhraseDetector _control;
    private readonly CoordinatorOptions _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _life = new();
    private readonly Task _loop;
    private readonly ITimer _ticker;

    private CommandBuffer? _buffer;
    private long? _finishStartMs;
    private long _finalizeGen;
    private long _runGen;
    private bool _runActive;
    private bool _reminded;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _lastRunEnd = DateTimeOffset.MinValue;
    private readonly List<string> _context = [];

    public AgentState State { get; private set; } = AgentState.Standby;
    public string DisplayTranscript { get; private set; } = "";
    public string InterimTranscript { get; private set; } = "";
    public bool AwaitingExecuteConfirmation { get; private set; }

    public event Action<AgentState>? StateChanged;
    public event Action<string, string>? TranscriptChanged; // (final, interim)
    public event Action<string>? Notice;
    public event Action<CommandEnvelope>? Committed;

    public ConversationCoordinator(ISpeechRecognizer recognizer, ISpeechSynthesizer? speech, ICommandSink sink, ControlPhraseDetector control, CoordinatorOptions options, TimeProvider time)
    {
        _recognizer = recognizer; _speech = speech; _sink = sink; _control = control; _options = options; _time = time;
        _recognizer.Transcript += e => _inbox.Writer.TryWrite(new Transcript(e));
        _recognizer.Error += m => _inbox.Writer.TryWrite(new RecognizerFailed(m));
        _ticker = time.CreateTimer(_ => _inbox.Writer.TryWrite(new Tick()), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _loop = Task.Run(LoopAsync);
    }

    // ---- public inputs (thread-safe) ----
    public void ActivateFromWake(string? trailingText = null) => _inbox.Writer.TryWrite(new Activate("wake", trailingText));
    public void ActivateFromHotkey() => _inbox.Writer.TryWrite(new Activate("hotkey"));
    public void PressProceed() => _inbox.Writer.TryWrite(new Proceed());
    public void PressCancel() => _inbox.Writer.TryWrite(new CancelPressed());
    public void SetMuted(bool muted) => _inbox.Writer.TryWrite(new Mute(muted));
    public void SetSessionLocked(bool locked) => _inbox.Writer.TryWrite(new Locked(locked));
    public void SubmitTyped(string text) => _inbox.Writer.TryWrite(new Typed(text));
    public void EditTranscript(string text) => _inbox.Writer.TryWrite(new Edit(text));
    public void ForceTick() => _inbox.Writer.TryWrite(new Tick());

    /// <summary>Waits until all previously posted inputs have been processed (tests/diagnostics).</summary>
    public Task DrainAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inbox.Writer.TryWrite(new Drain(tcs));
        return tcs.Task;
    }

    private async Task LoopAsync()
    {
        await foreach (var msg in _inbox.Reader.ReadAllAsync(_life.Token).ConfigureAwait(false))
        {
            try { await HandleAsync(msg).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Error("voice", $"coordinator failed handling {msg.GetType().Name} in state {State}", ex);
                Notice?.Invoke("Internal error: " + ex.GetType().Name);
                SetState(AgentState.Failed, "internal error"); SetState(AgentState.Standby, "recovered");
            }
        }
    }

    private async Task HandleAsync(Msg msg)
    {
        switch (msg)
        {
            case Drain d: d.Tcs.TrySetResult(); break;

            case Mute m:
                Log.Info("voice", $"input mute={m.On} state={State}");
                if (m.On)
                {
                    if (State is AgentState.Listening or AgentState.Clarifying or AgentState.Finalizing) await DiscardAsync("Muted. Your unfinished instruction was discarded.", "muted");
                    SetState(AgentState.Muted);
                }
                else if (State == AgentState.Muted) SetState(AgentState.Standby);
                break;

            case Locked l:
                Log.Info("voice", $"input session-locked={l.IsLocked} state={State} runActive={_runActive}");
                if (l.IsLocked)
                {
                    if (State is AgentState.Listening or AgentState.Clarifying or AgentState.Finalizing) await DiscardAsync("Screen locked. Listening stopped and the draft was discarded.", "screen locked");
                    if (_runActive) { await _sink.CancelActiveAsync("session-locked"); Notice?.Invoke("Screen locked — the task was stopped. No new actions will run."); }
                    if (State != AgentState.Muted) SetState(AgentState.Paused);
                }
                else if (State == AgentState.Paused && !_runActive) SetState(AgentState.Standby);
                break;

            case Activate a:
                Log.Info("voice", $"input activate source={a.Source} state={State} runActive={_runActive}" + (a.TrailingText is null ? "" : " trailing=" + Log.Text(a.TrailingText)));
                if (State is AgentState.Muted or AgentState.Paused) { Log.Info("voice", $"activate ignored: {State}"); break; }
                if (_runActive || State is AgentState.Listening or AgentState.Finalizing or AgentState.Clarifying) { Log.Info("voice", $"activate ignored: already busy ({State}, runActive={_runActive})"); break; } // no second task
                await StartListeningAsync(a.Source);
                if (!string.IsNullOrWhiteSpace(a.TrailingText) && _buffer is not null)
                {
                    // Words spoken in the same breath as the wake phrase ("Hey VoxStride, check C drive") are the start of the command.
                    var first = new TranscriptEvent(_buffer.SessionId, "wake-trailing", 0, 1, true, a.TrailingText.Trim(), -1, 0, null);
                    _buffer.Apply(first);
                    HandleFinal(first);
                    PublishTranscript();
                }
                break;

            case Transcript t:
                LogTranscript(t.Event);
                if (_buffer is null || t.Event.SessionId != _buffer.SessionId) { Log.Debug("voice", $"transcript dropped: stale session seg={t.Event.SegmentId}"); break; } // stale session
                if (State is not (AgentState.Listening or AgentState.Clarifying or AgentState.Finalizing)) { Log.Debug("voice", $"transcript dropped: state {State}"); break; }
                if (t.Event.IsFinal && _speech?.IsSpeaking == true) { Log.Info("voice", $"final ignored: VoxStride was speaking (own voice) seg={t.Event.SegmentId}"); break; }
                if (!_buffer.Apply(t.Event)) { Log.Debug("voice", $"transcript dropped by buffer seg={t.Event.SegmentId} rev={t.Event.Revision} (out of order or duplicate)"); break; }
                _lastActivity = _time.GetUtcNow(); _reminded = false;
                if (t.Event.IsFinal && State != AgentState.Finalizing) HandleFinal(t.Event);
                else if (t.Event.IsFinal && State == AgentState.Finalizing)
                {
                    // Late finals while finalizing: control words are ignored except cancel; content after the finish point is excluded by time.
                    var c = _control.Classify(t.Event.Text, t.Event.Confidence);
                    Log.Info("voice", $"late final while finalizing seg={t.Event.SegmentId} control={c.Kind}");
                    if (c.Kind is ControlKind.Cancel or ControlKind.Stop) { await DiscardAsync("Cancelled. Nothing was done.", "spoken cancel while finalizing"); break; }
                    if (c.Kind != ControlKind.Content) _buffer.MarkControl(t.Event.SegmentId, c.Content);
                }
                PublishTranscript();
                break;

            case Proceed:
                Log.Info("voice", $"input proceed state={State}");
                if (State is AgentState.Listening or AgentState.Clarifying)
                {
                    _naturalTurn = false;
                    AwaitingExecuteConfirmation = false;
                    BeginFinalize(null);
                }
                break;

            case CancelPressed:
                Log.Info("voice", $"input cancel state={State} runActive={_runActive}");
                if (State is AgentState.Listening or AgentState.Clarifying or AgentState.Finalizing) await DiscardAsync("Cancelled. Nothing was done.", "cancel pressed");
                else if (_runActive) { await _sink.CancelActiveAsync("user-cancel"); Notice?.Invoke("Stopping. No new actions will start."); }
                break;

            case Edit e:
                Log.Info("voice", $"input edit text={Log.Text(e.Text)}");
                if (_buffer is not null && State is AgentState.Listening or AgentState.Clarifying) { _buffer.SetManualText(e.Text); PublishTranscript(); }
                break;

            case Typed ty:
                Log.Info("voice", $"input typed state={State} text={Log.Text(ty.Text)}");
                if (_runActive || State is AgentState.Muted or AgentState.Paused or AgentState.Finalizing) { Log.Info("voice", "typed ignored: busy"); Notice?.Invoke("Busy — finish or cancel the current task first."); break; }
                if (State is AgentState.Listening or AgentState.Clarifying) { await _recognizer.StopAsync(TimeSpan.Zero, CancellationToken.None); _buffer = null; }
                Commit(ty.Text.Trim(), InputMode.Typed, Guid.NewGuid());
                break;

            case Tick:
                if (State is AgentState.Listening or AgentState.Clarifying)
                {
                    var idle = _time.GetUtcNow() - _lastActivity;
                    if (_options.NaturalTurnPause is { } pause && idle >= pause && !AwaitingExecuteConfirmation && _buffer is not null && _buffer.PendingCount == 0
                        && !string.IsNullOrWhiteSpace(_buffer.FinalText()))
                    {
                        Log.Info("voice", $"natural mode: end of turn after {idle.TotalSeconds:0.0}s pause (read-only turn)");
                        _naturalTurn = true;
                        BeginFinalize(null);
                        break;
                    }
                    if (idle >= _options.InactivityDiscard) await DiscardAsync("No instruction was finished, so I discarded the draft. Nothing was done.", $"inactive {idle.TotalSeconds:0}s");
                    else if (idle >= _options.InactivityReminder && !_reminded)
                    {
                        _reminded = true;
                        Log.Info("voice", $"inactivity reminder after {idle.TotalSeconds:0}s");
                        Notice?.Invoke("I'm still listening. Say \"VoxStride, execute\" or press Proceed when you're done.");
                    }
                }
                break;

            case FinalizeDone f:
                if (f.Generation != _finalizeGen || State != AgentState.Finalizing || _buffer is null) { Log.Debug("voice", $"finalize result ignored (gen {f.Generation}/{_finalizeGen}, state {State})"); break; }
                var text = _buffer.FinalText(_finishStartMs);
                Log.Info("voice", $"finalized pending={_buffer.PendingCount} finishAtMs={_finishStartMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "button"} text={Log.Text(text)}");
                if (_buffer.PendingCount > 0)
                {
                    SetState(AgentState.Clarifying, $"{_buffer.PendingCount} segment(s) still pending");
                    Notice?.Invoke("I may have missed your last words. Check the text and press Proceed, or keep speaking.");
                    await RestartRecognizerAsync();
                    break;
                }
                if (string.IsNullOrWhiteSpace(text) && !_naturalTurn && _lastNatural is { } ln && _time.GetUtcNow() - ln.At < _options.FollowUpWindow)
                {
                    // "VoxStride, execute" on its own confirms the request from the previous natural-mode turn.
                    Log.Info("voice", "explicit finish with no new words: confirming the previous natural-mode request");
                    _lastNatural = null;
                    Commit(ln.Text, InputMode.Voice, _buffer.SessionId);
                    break;
                }
                if (string.IsNullOrWhiteSpace(text))
                {
                    SetState(AgentState.Clarifying, "no words captured");
                    Notice?.Invoke("I didn't catch an instruction yet. Keep speaking, or type it.");
                    await RestartRecognizerAsync();
                    break;
                }
                Commit(text, _naturalTurn ? InputMode.VoiceNatural : InputMode.Voice, _buffer.SessionId);
                break;

            case RunDone r:
                if (r.Generation != _runGen) break;
                Log.Info("voice", $"run finished success={r.Summary.Success}");
                _runActive = false;
                _lastRunEnd = _time.GetUtcNow();
                if (State is not (AgentState.Muted or AgentState.Paused or AgentState.Listening)) SetState(AgentState.Standby, "run finished");
                break;

            case RecognizerFailed rf:
                Log.Warn("voice", $"recognizer error in state {State}: {rf.Message}");
                if (State is AgentState.Listening or AgentState.Finalizing or AgentState.Clarifying)
                {
                    SetState(AgentState.Clarifying, "recognizer error");
                    Notice?.Invoke($"Speech input problem: {rf.Message}. Your text so far is kept — press Proceed, edit it, or type instead.");
                }
                break;
        }
    }

    private void HandleFinal(TranscriptEvent e)
    {
        var c = _control.Classify(e.Text, e.Confidence);
        if (c.Kind != ControlKind.Content) Log.Info("voice", $"control phrase {c.Kind} in seg={e.SegmentId} conf={Conf(e.Confidence)}" + (c.Content is null ? "" : " remainder=" + Log.Text(c.Content)));
        switch (c.Kind)
        {
            case ControlKind.Content:
                if (AwaitingExecuteConfirmation) { AwaitingExecuteConfirmation = false; SetState(AgentState.Listening, "kept talking"); }
                break;
            case ControlKind.Finish:
                _naturalTurn = false;
                _buffer!.MarkControl(e.SegmentId, c.Content);
                AwaitingExecuteConfirmation = false;
                BeginFinalize(string.IsNullOrWhiteSpace(c.Content) ? e.StartMs : e.StartMs + 1);
                break;
            case ControlKind.FinishUncertain:
                if (c.Content is not null) _buffer!.MarkControl(e.SegmentId, e.Text); // keep full text as content
                AwaitingExecuteConfirmation = true;
                SetState(AgentState.Clarifying, "finish phrase uncertain");
                Notice?.Invoke("Ready to execute? Press Proceed or say \"VoxStride, execute\". Otherwise keep talking.");
                break;
            case ControlKind.Cancel:
            case ControlKind.Stop:
                _ = DiscardAsync("Cancelled. Nothing was done.", "spoken " + c.Kind);
                break;
            case ControlKind.UndoLast:
                _buffer!.MarkControl(e.SegmentId);
                if (!_buffer.UndoLast()) Notice?.Invoke("Nothing to remove.");
                break;
            case ControlKind.Replace:
                _buffer!.MarkControl(e.SegmentId);
                if (!_buffer.ReplaceLast(c.Content ?? "")) Notice?.Invoke("Nothing to replace.");
                break;
            case ControlKind.Pause:
                _buffer!.MarkControl(e.SegmentId);
                break;
        }
    }

    private async Task StartListeningAsync(string source)
    {
        _naturalTurn = false;
        var session = Guid.NewGuid();
        _buffer = new CommandBuffer(session);
        _finishStartMs = null;
        _reminded = false;
        AwaitingExecuteConfirmation = false;
        _lastActivity = _time.GetUtcNow();
        _speech?.StopSpeaking();
        _speech?.PlayCue(CueKind.Wake);
        SetState(AgentState.Listening, "activated by " + source);
        PublishTranscript();
        Log.Info("voice", $"listening session={Short(session)} recognizer=\"{_recognizer.Name}\"");
        try
        {
            using var t = Log.Time("voice", "recognizer start");
            await _recognizer.StartAsync(session, _life.Token);
        }
        catch (Exception ex)
        {
            Log.Error("voice", "recognizer failed to start", ex);
            SetState(AgentState.Clarifying, "recognizer failed to start");
            Notice?.Invoke("Couldn't start speech recognition (" + ex.Message + "). You can type your instruction instead.");
        }
    }

    private async Task RestartRecognizerAsync()
    {
        if (_buffer is null) return;
        Log.Info("voice", $"restarting recognizer session={Short(_buffer.SessionId)}");
        try { await _recognizer.StartAsync(_buffer.SessionId, _life.Token); }
        catch (Exception ex) { Log.Error("voice", "recognizer failed to restart", ex); Notice?.Invoke("Speech input unavailable: " + ex.Message); }
    }

    private bool _naturalTurn;
    private (string Text, DateTimeOffset At)? _lastNatural;

    private void BeginFinalize(long? finishStartMs)
    {
        _finishStartMs = finishStartMs;
        var gen = ++_finalizeGen;
        SetState(AgentState.Finalizing, finishStartMs is null ? "proceed pressed" : "finish phrase");
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { await _recognizer.StopAsync(_options.FinalizeFlushTimeout, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("voice", "recognizer stop failed while finalizing", ex); /* finalize still proceeds with what we have; pending interims block commit */ }
            Log.Info("voice", $"recognizer flushed in {sw.ElapsedMilliseconds} ms (limit {_options.FinalizeFlushTimeout.TotalMilliseconds:0} ms)");
            _inbox.Writer.TryWrite(new FinalizeDone(gen));
        });
    }

    private void Commit(string text, InputMode mode, Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _lastNatural = mode == InputMode.VoiceNatural ? (text, _time.GetUtcNow()) : null;
        var withinFollowUp = _time.GetUtcNow() - _lastRunEnd < _options.FollowUpWindow;
        var envelope = new CommandEnvelope(sessionId, Guid.NewGuid(), text, RunAuthority.HashTranscript(text), _options.Language,
            withinFollowUp ? [.. _context.TakeLast(4)] : [], _time.GetUtcNow(), mode, _options.SelectedProvider(), null);
        _buffer = null;
        Log.Info("voice", $"commit mode={mode} session={Short(sessionId)} command={Short(envelope.CommandId)} provider={envelope.SelectedProvider ?? "none"} followUpContext={envelope.ConversationContext.Count} text={Log.Text(text)}");
        _speech?.PlayCue(CueKind.Commit);
        SetState(AgentState.Committed, mode + " commit");
        Committed?.Invoke(envelope);
        _runActive = true;
        var gen = ++_runGen;
        _ = Task.Run(async () =>
        {
            RunSummary summary;
            try { summary = await _sink.RunAsync(envelope, _life.Token).ConfigureAwait(false); }
            catch (Exception ex) { Log.Error("voice", "run threw", ex); summary = new RunSummary(false, "The task failed: " + ex.Message); }
            lock (_context)
            {
                _context.Add("User: " + text);
                _context.Add("Result: " + summary.SpokenSummary);
                if (_context.Count > 8) _context.RemoveRange(0, _context.Count - 8);
            }
            _inbox.Writer.TryWrite(new RunDone(gen, summary));
        });
    }

    private async Task DiscardAsync(string notice, string reason)
    {
        Log.Info("voice", $"discard reason={reason} draft={Log.Text(_buffer?.FinalText())}");
        _buffer = null;
        _finalizeGen++;
        AwaitingExecuteConfirmation = false;
        try { await _recognizer.StopAsync(TimeSpan.Zero, CancellationToken.None); } catch (Exception ex) { Log.Warn("voice", "recognizer stop failed while discarding", ex); }
        DisplayTranscript = ""; InterimTranscript = "";
        TranscriptChanged?.Invoke("", "");
        _speech?.PlayCue(CueKind.Cancelled);
        Notice?.Invoke(notice);
        SetState(AgentState.Cancelled, reason);
        SetState(AgentState.Standby, "after cancel");
    }

    private void PublishTranscript()
    {
        DisplayTranscript = _buffer?.FinalText() ?? "";
        InterimTranscript = _buffer?.InterimText ?? "";
        TranscriptChanged?.Invoke(DisplayTranscript, InterimTranscript);
    }

    private void SetState(AgentState s, string? reason = null)
    {
        if (State == s) return;
        Log.Info("voice", $"state {State} -> {s}" + (reason is null ? "" : $" ({reason})"));
        State = s;
        StateChanged?.Invoke(s);
    }

    private static void LogTranscript(TranscriptEvent e)
    {
        if (e.IsFinal) Log.Info("voice", $"heard final seg={e.SegmentId} rev={e.Revision} conf={Conf(e.Confidence)} audio={e.StartMs}-{e.EndMs}ms text={Log.Text(e.Text)}");
        else if (Log.IsEnabled(LogLevel.Debug)) Log.Debug("voice", $"heard partial seg={e.SegmentId} rev={e.Revision} text={Log.Text(e.Text, 160)}");
    }

    private static string Conf(double? c) => c is { } v ? v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "n/a";
    private static string Short(Guid g) => g.ToString("N")[..8];

    public async ValueTask DisposeAsync()
    {
        await _ticker.DisposeAsync();
        _inbox.Writer.TryComplete();
        await _life.CancelAsync();
        try { await _loop; } catch (OperationCanceledException) { }
        _life.Dispose();
    }
}
