using VoxStride.Contracts;
using VoxStride.Core.Voice;
using Xunit;

namespace VoxStride.Tests;

public sealed class FakeRecognizer : ISpeechRecognizer
{
    public Guid Session { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public bool Running { get; private set; }
    public List<TranscriptEvent> OnStopFlush { get; } = [];
    private long _seq;
    public string Name => "fake";
    public event Action<TranscriptEvent>? Transcript;
    public event Action<string>? Error;
    public Task StartAsync(Guid sessionId, CancellationToken ct) { Session = sessionId; Starts++; Running = true; return Task.CompletedTask; }
    public Task StopAsync(TimeSpan flushTimeout, CancellationToken ct)
    {
        Stops++; Running = false;
        foreach (var e in OnStopFlush) Transcript?.Invoke(e with { SessionId = Session });
        OnStopFlush.Clear();
        return Task.CompletedTask;
    }
    public void Say(string id, string text, long startMs, bool final = true, int rev = 1, double? conf = null) =>
        Transcript?.Invoke(new TranscriptEvent(Session, id, Interlocked.Increment(ref _seq), rev, final, text, startMs, startMs + 800, conf));
    public void Fail(string m) => Error?.Invoke(m);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeSink : ICommandSink
{
    public List<CommandEnvelope> Commands { get; } = [];
    public int Cancels { get; private set; }
    public TaskCompletionSource<bool>? Gate { get; set; }
    public async Task<RunSummary> RunAsync(CommandEnvelope command, CancellationToken ct)
    {
        lock (Commands) Commands.Add(command);
        if (Gate is not null) await Gate.Task;
        return new RunSummary(true, "done");
    }
    public Task CancelActiveAsync(string reason) { Cancels++; Gate?.TrySetResult(false); return Task.CompletedTask; }
}

public sealed class CoordinatorTests : IAsyncDisposable
{
    private readonly FakeRecognizer _rec = new();
    private readonly FakeSink _sink = new();
    private readonly ManualTime _time = new();
    private readonly ConversationCoordinator _c;
    private readonly List<string> _notices = [];

    public CoordinatorTests()
    {
        _c = new ConversationCoordinator(_rec, null, _sink, new ControlPhraseDetector(new ControlPhraseOptions()), new CoordinatorOptions(), _time);
        _c.Notice += n => { lock (_notices) _notices.Add(n); };
    }

    public ValueTask DisposeAsync() => _c.DisposeAsync();

    private async Task Settle()
    {
        for (var i = 0; i < 5; i++) { await _c.DrainAsync(); await Task.Delay(5); }
    }

    private async Task Wake() { _c.ActivateFromHotkey(); await Settle(); }

    private async Task Pause(TimeSpan t)
    {
        await Settle(); // ensure earlier speech is processed at the current time before time advances
        var step = TimeSpan.FromSeconds(1);
        for (var e = TimeSpan.Zero; e < t; e += step) { _time.Advance(step); _c.ForceTick(); }
        await Settle();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(90)]
    public async Task V01_PausesNeverCommit(int seconds)
    {
        await Wake();
        _rec.Say("a", "check how much space is left on C", 0);
        await Pause(TimeSpan.FromSeconds(seconds));
        _rec.Say("b", "also tell me total capacity", 1000 * (seconds + 2));
        await Pause(TimeSpan.FromSeconds(seconds));
        Assert.Empty(_sink.Commands);
        Assert.Equal(AgentState.Listening, _c.State);
        Assert.Equal("check how much space is left on C. also tell me total capacity", _c.DisplayTranscript);
    }

    [Fact]
    public async Task ShortYesAfterReadyToExecuteCommitsWithoutTheAnswerWord()
    {
        // Owner's log: "... search for cricket. Proceed" -> "Ready to execute?" -> nothing happened until the button.
        await Wake();
        _rec.Say("a", "open the Chrome browser and search for cricket", 0);
        _rec.Say("b", "Proceed", 5000);
        await Settle();
        Assert.Empty(_sink.Commands);
        Assert.True(_c.AwaitingExecuteConfirmation);
        _rec.Say("n", "", 7000);                      // a cough between the question and the answer
        await Settle();
        Assert.True(_c.AwaitingExecuteConfirmation);
        _rec.Say("c", "Yes, proceed.", 9000);
        await Settle();
        var cmd = Assert.Single(_sink.Commands);
        Assert.DoesNotContain("Yes", cmd.FinalizedTranscript);
        Assert.StartsWith("open the Chrome browser and search for cricket", cmd.FinalizedTranscript);
    }

    [Fact]
    public async Task RealContentAfterTheQuestionMeansKeepTalking()
    {
        await Wake();
        _rec.Say("a", "write a note", 0);
        _rec.Say("b", "execute", 3000);
        await Settle();
        Assert.True(_c.AwaitingExecuteConfirmation);
        _rec.Say("c", "about the meeting tomorrow", 5000);
        await Settle();
        Assert.False(_c.AwaitingExecuteConfirmation);
        Assert.Equal(AgentState.Listening, _c.State);
        _rec.Say("d", "yes", 8000);                   // without a pending question, "yes" is just content
        await Settle();
        Assert.Empty(_sink.Commands);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("Yes, proceed.", true)]
    [InlineData("VoxStride proceed", true)]
    [InlineData("haan ji", true)]
    [InlineData("go ahead", true)]
    [InlineData("yes and also open notepad", false)]
    [InlineData("proceed with the cleanup of my desktop", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    public void ConfirmationWordsAreShortAndStandalone(string text, bool expected) =>
        Assert.Equal(expected, ConversationCoordinator.IsConfirmation(text));

    [Fact]
    public async Task ExplicitFinishCommitsEverythingExactlyOnce()
    {
        await Wake();
        _rec.Say("a", "Check how much space is left on C", 0);
        await Pause(TimeSpan.FromSeconds(15));
        _rec.Say("b", "also tell me total capacity", 16000);
        _rec.Say("c", "VoxStride, execute", 20000);
        await Settle();
        var cmd = Assert.Single(_sink.Commands);
        Assert.Equal("Check how much space is left on C. also tell me total capacity", cmd.FinalizedTranscript);
        Assert.Equal(InputMode.Voice, cmd.InputMode);
        Assert.True(_rec.Stops >= 1);
    }

    [Fact]
    public async Task LateFinalBeforeFinishIsIncluded_AudioAfterFinishIsNot()
    {
        await Wake();
        _rec.Say("a", "organize my desktop", 0);
        _rec.Say("b", "leave shortcuts", 3000, final: false);
        _rec.OnStopFlush.Add(new TranscriptEvent(Guid.Empty, "b", 99, 2, true, "leave shortcuts alone", 3000, 3800));
        _rec.OnStopFlush.Add(new TranscriptEvent(Guid.Empty, "tv", 100, 1, true, "buy now and delete files", 9000, 9800));
        _rec.Say("fin", "voxstride execute", 6000);
        await Settle();
        Assert.Equal("organize my desktop. leave shortcuts alone", Assert.Single(_sink.Commands).FinalizedTranscript);
    }

    [Fact]
    public async Task PendingInterimAfterFlushBlocksCommit()
    {
        await Wake();
        _rec.Say("a", "organize my desktop", 0);
        _rec.Say("b", "but leave the", 2000, final: false);
        _c.PressProceed();
        await Settle();
        Assert.Empty(_sink.Commands);
        Assert.Equal(AgentState.Clarifying, _c.State);
    }

    [Fact]
    public async Task V02_QuotedProceedDoesNotCommit()
    {
        await Wake();
        _rec.Say("a", "write the sentence 'please proceed' in Notepad++", 0);
        _rec.Say("b", "proceed", 3000);
        await Settle();
        Assert.Empty(_sink.Commands);
    }

    [Fact]
    public async Task UncertainFinishAsksThenButtonCommits()
    {
        await Wake();
        _rec.Say("a", "write the sentence voxstride execute", 0);
        await Settle();
        Assert.Empty(_sink.Commands);
        Assert.True(_c.AwaitingExecuteConfirmation);
        _c.PressProceed();
        await Settle();
        Assert.Equal("write the sentence voxstride execute", Assert.Single(_sink.Commands).FinalizedTranscript);
    }

    [Fact]
    public async Task V03_CorrectionIsPreservedVerbatimForPlanning()
    {
        await Wake();
        _rec.Say("a", "move my PDFs into a folder", 0);
        _rec.Say("b", "actually leave PDFs alone and move pictures", 4000);
        _rec.Say("c", "voxstride execute", 8000);
        await Settle();
        Assert.Contains("actually leave PDFs alone and move pictures", Assert.Single(_sink.Commands).FinalizedTranscript);
    }

    [Fact]
    public async Task ScratchThatRemovesLastSentence()
    {
        await Wake();
        _rec.Say("a", "organize my desktop", 0);
        _rec.Say("b", "and delete everything", 2000);
        _rec.Say("c", "scratch that", 4000);
        _rec.Say("d", "voxstride execute", 6000);
        await Settle();
        Assert.Equal("organize my desktop", Assert.Single(_sink.Commands).FinalizedTranscript);
    }

    [Fact]
    public async Task ReminderAt30sAndDiscardAt120sWithoutExecution()
    {
        await Wake();
        _rec.Say("a", "organize my desktop", 0);
        await Pause(TimeSpan.FromSeconds(31));
        Assert.Contains(_notices, n => n.Contains("still listening", StringComparison.Ordinal));
        await Pause(TimeSpan.FromSeconds(95));
        Assert.Empty(_sink.Commands);
        Assert.Equal(AgentState.Standby, _c.State);
        Assert.Contains(_notices, n => n.Contains("discarded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V05_RecognizerFailureKeepsDraftAndNeverCommits()
    {
        await Wake();
        _rec.Say("a", "organize my desktop", 0);
        _rec.Fail("Microphone disconnected");
        await Pause(TimeSpan.FromSeconds(10));
        Assert.Empty(_sink.Commands);
        Assert.Equal(AgentState.Clarifying, _c.State);
        Assert.Equal("organize my desktop", _c.DisplayTranscript);
    }

    [Fact]
    public async Task V05_LockAndMuteDiscardDraft()
    {
        await Wake();
        _rec.Say("a", "organize my desktop", 0);
        _c.SetSessionLocked(true);
        await Settle();
        Assert.Equal(AgentState.Paused, _c.State);
        Assert.Empty(_sink.Commands);
        _c.SetSessionLocked(false);
        await Settle();
        Assert.Equal(AgentState.Standby, _c.State);

        await Wake();
        _rec.Say("b", "organize my desktop", 0);
        _c.SetMuted(true);
        await Settle();
        Assert.Equal(AgentState.Muted, _c.State);
        _c.ActivateFromHotkey();
        await Settle();
        Assert.Equal(AgentState.Muted, _c.State); // wake ignored while muted
        Assert.Empty(_sink.Commands);
    }

    [Fact]
    public async Task CancelPhraseDiscards()
    {
        await Wake();
        _rec.Say("a", "organize my desktop", 0);
        _rec.Say("b", "never mind", 2000);
        await Settle();
        Assert.Empty(_sink.Commands);
        Assert.Equal(AgentState.Standby, _c.State);
    }

    [Fact]
    public async Task StaleSessionTranscriptsAreIgnored()
    {
        await Wake();
        var old = _rec.Session;
        _c.PressCancel();
        await Settle();
        await Wake();
        _rec.Say("a", "check C", 0);
        // event from the previous session arrives late
        typeof(FakeRecognizer).GetProperty("Session")!.SetValue(_rec, old);
        _rec.Say("x", "delete everything", 100);
        await Settle();
        Assert.DoesNotContain("delete", _c.DisplayTranscript);
    }

    [Fact]
    public async Task TypedCommandCommitsWithoutMicrophone()
    {
        _c.SubmitTyped("check C drive");
        await Settle();
        var cmd = Assert.Single(_sink.Commands);
        Assert.Equal(InputMode.Typed, cmd.InputMode);
    }

    [Fact]
    public async Task SecondActivationDuringRunIsIgnoredAndCancelReachesRun()
    {
        _sink.Gate = new TaskCompletionSource<bool>();
        _c.SubmitTyped("organize desktop");
        await Settle();
        _c.ActivateFromHotkey();
        await Settle();
        Assert.NotEqual(AgentState.Listening, _c.State);
        _c.PressCancel();
        await Settle();
        Assert.Equal(1, _sink.Cancels);
    }

    /// <summary>
    /// V01–V03 gate: 240 scripted trials mixing long pauses, corrections, quoted control words, interims and
    /// noise. Asserts zero premature commits and that every intended commit contains all retained content.
    /// </summary>
    [Fact]
    public async Task Gate_240ScriptedTrials_ZeroPrematureCommits()
    {
        var rng = new Random(20261126);
        string[] content = ["organize my desktop", "leave the zip files alone", "check how much space is left on C", "also tell me total capacity",
            "write a short paragraph about solar power", "in Notepad++", "write the sentence 'please proceed'", "type the words go ahead", "proceed with caution is a phrase",
            "actually leave PDFs alone", "and move pictures instead", "then play the third video in the top row"];
        var premature = 0; var missing = 0;
        for (var trial = 0; trial < 240; trial++)
        {
            await using var rec = new FakeRecognizerBox();
            var sink = new FakeSink();
            var time = new ManualTime();
            await using var c = new ConversationCoordinator(rec.R, null, sink, new ControlPhraseDetector(new ControlPhraseOptions()), new CoordinatorOptions(), time);
            c.ActivateFromHotkey();
            await c.DrainAsync();
            var n = rng.Next(1, 6);
            var expected = new List<string>();
            long t = 0;
            for (var i = 0; i < n; i++)
            {
                var s = content[rng.Next(content.Length)];
                if (rng.Next(3) == 0) rec.R.Say("i" + i, s[..Math.Max(1, s.Length / 2)], t, final: false, rev: 1);
                rec.R.Say("i" + i, s, t, final: true, rev: 2);
                expected.Add(s);
                var pause = rng.Next(0, 60);
                for (var p = 0; p < pause; p++) { time.Advance(TimeSpan.FromSeconds(1)); c.ForceTick(); }
                t += 1000 * (pause + 3);
                await c.DrainAsync();
                if (sink.Commands.Count > 0) premature++;
            }
            if (sink.Commands.Count > 0) { premature++; continue; }
            rec.R.Say("fin", "VoxStride, execute", t);
            for (var k = 0; k < 4; k++) { await c.DrainAsync(); await Task.Delay(1); }
            if (sink.Commands.Count != 1) { missing++; continue; }
            var text = sink.Commands[0].FinalizedTranscript;
            if (expected.Any(e => !text.Contains(e, StringComparison.Ordinal))) missing++;
        }
        Assert.Equal(0, premature);
        Assert.Equal(0, missing);
    }

    private sealed class FakeRecognizerBox : IAsyncDisposable
    {
        public FakeRecognizer R { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class WakeTrailingTextTests : IAsyncDisposable
{
    private readonly FakeRecognizer _rec = new();
    private readonly FakeSink _sink = new();
    private readonly ConversationCoordinator _c;
    public WakeTrailingTextTests() => _c = new ConversationCoordinator(_rec, null, _sink, new ControlPhraseDetector(new ControlPhraseOptions()), new CoordinatorOptions(), new ManualTime());
    public ValueTask DisposeAsync() => _c.DisposeAsync();

    [Fact]
    public async Task WordsAfterWakePhraseStartTheCommandButDoNotCommit()
    {
        _c.ActivateFromWake("check how much space is left on C");
        for (var i = 0; i < 3; i++) await _c.DrainAsync();
        Assert.Empty(_sink.Commands);
        _rec.Say("b", "VoxStride execute", 5000);
        for (var i = 0; i < 4; i++) { await _c.DrainAsync(); await Task.Delay(5); }
        Assert.Equal("check how much space is left on C", Assert.Single(_sink.Commands).FinalizedTranscript);
    }

    [Fact]
    public async Task WakeAndFinishInOneBreathCommitsOnce()
    {
        _c.ActivateFromWake("open notepad VoxStride execute");
        for (var i = 0; i < 4; i++) { await _c.DrainAsync(); await Task.Delay(5); }
        Assert.Equal("open notepad", Assert.Single(_sink.Commands).FinalizedTranscript);
    }
}
