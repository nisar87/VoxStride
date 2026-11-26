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
}

/// <summary>
/// Single-reader actor that owns the voice/command lifecycle (spec §14.2). It is the only component that can
/// create a <see cref="CommandEnvelope"/>. Silence, timeouts, reconnects and ambient audio never commit.
/// </summary>
public sealed class ConversationCoordinator : IAsyncDisposable
{
    private abstract record Msg;
    private sealed record Activate(string Source) : Msg;
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
    public void ActivateFromWake() => _inbox.Writer.TryWrite(new Activate("wake"));
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
            catch (Exception ex) { Notice?.Invoke("Internal error: " + ex.GetType().Name); SetState(AgentState.Failed); SetState(AgentState.Standby); }
        }
    }

    private async Task HandleAsync(Msg msg)
    {
        switch (msg)
        {
            case Drain d: d.Tcs.TrySetResult(); break;

            case Mute m:
                if (m.On)
                {
                    if (State is AgentState.Listening or AgentState.Clarifying or AgentState.Finalizing) await DiscardAsync("Muted. Your unfinished instruction was discarded.");
                    SetState(AgentState.Muted);
                }
                else if (State == AgentState.Muted) SetState(AgentState.Standby);
                break;

            case Locked l:
                if (l.IsLocked)
                {
                    if (State is AgentState.Listening or AgentState.Clarifying or AgentState.Finalizing) await DiscardAsync("Screen locked. Listening stopped and the draft was discarded.");
                    if (_runActive) { await _sink.CancelActiveAsync("session-locked"); Notice?.Invoke("Screen locked — the task was stopped. No new actions will run."); }
                    if (State != AgentState.Muted) SetState(AgentState.Paused);
                }
                else if (State == AgentState.Paused && !_runActive) SetState(AgentState.Standby);
                break;

            case Activate a:
                if (State is AgentState.Muted or AgentState.Paused) break;
                if (_runActive || State is AgentState.Listening or AgentState.Finalizing or AgentState.Clarifying) break; // no second task
                await StartListeningAsync();
                break;

            case Transcript t:
                if (_buffer is null || t.Event.SessionId != _buffer.SessionId) break; // stale session
                if (State is not (AgentState.Listening or AgentState.Clarifying or AgentState.Finalizing)) break;
                if (!_buffer.Apply(t.Event)) break;
                _lastActivity = _time.GetUtcNow(); _reminded = false;
                if (t.Event.IsFinal && State != AgentState.Finalizing) HandleFinal(t.Event);
                else if (t.Event.IsFinal && State == AgentState.Finalizing)
                {
                    // Late finals while finalizing: control words are ignored except cancel; content after the finish point is excluded by time.
                    var c = _control.Classify(t.Event.Text, t.Event.Confidence);
                    if (c.Kind is ControlKind.Cancel or ControlKind.Stop) { await DiscardAsync("Cancelled. Nothing was done."); break; }
                    if (c.Kind != ControlKind.Content) _buffer.MarkControl(t.Event.SegmentId, c.Content);
                }
                PublishTranscript();
                break;

            case Proceed:
                if (State is AgentState.Listening or AgentState.Clarifying)
                {
                    AwaitingExecuteConfirmation = false;
                    BeginFinalize(null);
                }
                break;

            case CancelPressed:
                if (State is AgentState.Listening or AgentState.Clarifying or AgentState.Finalizing) await DiscardAsync("Cancelled. Nothing was done.");
                else if (_runActive) { await _sink.CancelActiveAsync("user-cancel"); Notice?.Invoke("Stopping. No new actions will start."); }
                break;

            case Edit e:
                if (_buffer is not null && State is AgentState.Listening or AgentState.Clarifying) { _buffer.SetManualText(e.Text); PublishTranscript(); }
                break;

            case Typed ty:
                if (_runActive || State is AgentState.Muted or AgentState.Paused or AgentState.Finalizing) { Notice?.Invoke("Busy — finish or cancel the current task first."); break; }
                if (State is AgentState.Listening or AgentState.Clarifying) { await _recognizer.StopAsync(TimeSpan.Zero, CancellationToken.None); _buffer = null; }
                Commit(ty.Text.Trim(), InputMode.Typed, Guid.NewGuid());
                break;

            case Tick:
                if (State is AgentState.Listening or AgentState.Clarifying)
                {
                    var idle = _time.GetUtcNow() - _lastActivity;
                    if (idle >= _options.InactivityDiscard) await DiscardAsync("No instruction was finished, so I discarded the draft. Nothing was done.");
                    else if (idle >= _options.InactivityReminder && !_reminded)
                    {
                        _reminded = true;
                        Notice?.Invoke("I'm still listening. Say \"VoxStride, execute\" or press Proceed when you're done.");
                    }
                }
                break;

            case FinalizeDone f:
                if (f.Generation != _finalizeGen || State != AgentState.Finalizing || _buffer is null) break;
                var text = _buffer.FinalText(_finishStartMs);
                if (_buffer.PendingCount > 0)
                {
                    SetState(AgentState.Clarifying);
                    Notice?.Invoke("I may have missed your last words. Check the text and press Proceed, or keep speaking.");
                    await RestartRecognizerAsync();
                    break;
                }
                if (string.IsNullOrWhiteSpace(text))
                {
                    SetState(AgentState.Clarifying);
                    Notice?.Invoke("I didn't catch an instruction yet. Keep speaking, or type it.");
                    await RestartRecognizerAsync();
                    break;
                }
                Commit(text, InputMode.Voice, _buffer.SessionId);
                break;

            case RunDone r:
                if (r.Generation != _runGen) break;
                _runActive = false;
                _lastRunEnd = _time.GetUtcNow();
                if (State is not (AgentState.Muted or AgentState.Paused or AgentState.Listening)) SetState(AgentState.Standby);
                break;

            case RecognizerFailed rf:
                if (State is AgentState.Listening or AgentState.Finalizing or AgentState.Clarifying)
                {
                    SetState(AgentState.Clarifying);
                    Notice?.Invoke($"Speech input problem: {rf.Message}. Your text so far is kept — press Proceed, edit it, or type instead.");
                }
                break;
        }
    }

    private void HandleFinal(TranscriptEvent e)
    {
        var c = _control.Classify(e.Text, e.Confidence);
        switch (c.Kind)
        {
            case ControlKind.Content:
                if (AwaitingExecuteConfirmation) { AwaitingExecuteConfirmation = false; SetState(AgentState.Listening); }
                break;
            case ControlKind.Finish:
                _buffer!.MarkControl(e.SegmentId, c.Content);
                AwaitingExecuteConfirmation = false;
                BeginFinalize(string.IsNullOrWhiteSpace(c.Content) ? e.StartMs : e.StartMs + 1);
                break;
            case ControlKind.FinishUncertain:
                if (c.Content is not null) _buffer!.MarkControl(e.SegmentId, e.Text); // keep full text as content
                AwaitingExecuteConfirmation = true;
                SetState(AgentState.Clarifying);
                Notice?.Invoke("Ready to execute? Press Proceed or say \"VoxStride, execute\". Otherwise keep talking.");
                break;
            case ControlKind.Cancel:
            case ControlKind.Stop:
                _ = DiscardAsync("Cancelled. Nothing was done.");
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

    private async Task StartListeningAsync()
    {
        var session = Guid.NewGuid();
        _buffer = new CommandBuffer(session);
        _finishStartMs = null;
        _reminded = false;
        AwaitingExecuteConfirmation = false;
        _lastActivity = _time.GetUtcNow();
        _speech?.StopSpeaking();
        _speech?.PlayCue(CueKind.Wake);
        SetState(AgentState.Listening);
        PublishTranscript();
        try { await _recognizer.StartAsync(session, _life.Token); }
        catch (Exception ex)
        {
            SetState(AgentState.Clarifying);
            Notice?.Invoke("Couldn't start speech recognition (" + ex.Message + "). You can type your instruction instead.");
        }
    }

    private async Task RestartRecognizerAsync()
    {
        if (_buffer is null) return;
        try { await _recognizer.StartAsync(_buffer.SessionId, _life.Token); }
        catch (Exception ex) { Notice?.Invoke("Speech input unavailable: " + ex.Message); }
    }

    private void BeginFinalize(long? finishStartMs)
    {
        _finishStartMs = finishStartMs;
        var gen = ++_finalizeGen;
        SetState(AgentState.Finalizing);
        _ = Task.Run(async () =>
        {
            try { await _recognizer.StopAsync(_options.FinalizeFlushTimeout, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* finalize still proceeds with what we have; pending interims block commit */ }
            _inbox.Writer.TryWrite(new FinalizeDone(gen));
        });
    }

    private void Commit(string text, InputMode mode, Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var withinFollowUp = _time.GetUtcNow() - _lastRunEnd < _options.FollowUpWindow;
        var envelope = new CommandEnvelope(sessionId, Guid.NewGuid(), text, RunAuthority.HashTranscript(text), _options.Language,
            withinFollowUp ? [.. _context.TakeLast(4)] : [], _time.GetUtcNow(), mode, _options.SelectedProvider(), null);
        _buffer = null;
        _speech?.PlayCue(CueKind.Commit);
        SetState(AgentState.Committed);
        Committed?.Invoke(envelope);
        _runActive = true;
        var gen = ++_runGen;
        _ = Task.Run(async () =>
        {
            RunSummary summary;
            try { summary = await _sink.RunAsync(envelope, _life.Token).ConfigureAwait(false); }
            catch (Exception ex) { summary = new RunSummary(false, "The task failed: " + ex.Message); }
            lock (_context)
            {
                _context.Add("User: " + text);
                _context.Add("Result: " + summary.SpokenSummary);
                if (_context.Count > 8) _context.RemoveRange(0, _context.Count - 8);
            }
            _inbox.Writer.TryWrite(new RunDone(gen, summary));
        });
    }

    private async Task DiscardAsync(string notice)
    {
        _buffer = null;
        _finalizeGen++;
        AwaitingExecuteConfirmation = false;
        try { await _recognizer.StopAsync(TimeSpan.Zero, CancellationToken.None); } catch (Exception) { }
        DisplayTranscript = ""; InterimTranscript = "";
        TranscriptChanged?.Invoke("", "");
        _speech?.PlayCue(CueKind.Cancelled);
        Notice?.Invoke(notice);
        SetState(AgentState.Cancelled);
        SetState(AgentState.Standby);
    }

    private void PublishTranscript()
    {
        DisplayTranscript = _buffer?.FinalText() ?? "";
        InterimTranscript = _buffer?.InterimText ?? "";
        TranscriptChanged?.Invoke(DisplayTranscript, InterimTranscript);
    }

    private void SetState(AgentState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    public async ValueTask DisposeAsync()
    {
        await _ticker.DisposeAsync();
        _inbox.Writer.TryComplete();
        await _life.CancelAsync();
        try { await _loop; } catch (OperationCanceledException) { }
        _life.Dispose();
    }
}
