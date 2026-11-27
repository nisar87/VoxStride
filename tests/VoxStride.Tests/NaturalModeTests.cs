using VoxStride.Contracts;
using VoxStride.Core.Agent;
using VoxStride.Core.Events;
using VoxStride.Core.Routing;
using VoxStride.Core.Voice;
using Xunit;

namespace VoxStride.Tests;

/// <summary>
/// Opt-in natural mode: a pause may end a turn, but such a turn can only read and answer. Changes still need an
/// explicit "VoxStride, execute" / Proceed (CLAUDE.md: silence never commits an action).
/// </summary>
public sealed class NaturalModeTests
{
    private sealed class SpeakingFake : ISpeechSynthesizer
    {
        public bool IsSpeaking { get; set; }
        public Task SpeakAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public void PlayCue(CueKind cue) { }
        public void StopSpeaking() { }
    }

    private static ConversationCoordinator Make(FakeRecognizer rec, FakeSink sink, ManualTime time, TimeSpan? pause, ISpeechSynthesizer? speech = null) =>
        new(rec, speech, sink, new ControlPhraseDetector(new ControlPhraseOptions()), new CoordinatorOptions { NaturalTurnPause = pause }, time);

    private static async Task Settle(ConversationCoordinator c, FakeSink sink, int expected)
    {
        for (var i = 0; i < 100 && sink.Commands.Count < expected; i++) { await Task.Delay(10); await c.DrainAsync(); }
    }

    [Fact]
    public async Task OffByDefault_APauseNeverCommits()
    {
        var rec = new FakeRecognizer(); var sink = new FakeSink(); var time = new ManualTime();
        await using var c = Make(rec, sink, time, null);
        c.ActivateFromHotkey(); await c.DrainAsync();
        rec.Say("s1", "how much space is on C", 0); await c.DrainAsync();
        time.Advance(TimeSpan.FromSeconds(20)); c.ForceTick(); await c.DrainAsync();
        await Task.Delay(50); await c.DrainAsync();
        Assert.Empty(sink.Commands);
    }

    [Fact]
    public async Task PauseEndsTheTurnAsReadOnly()
    {
        var rec = new FakeRecognizer(); var sink = new FakeSink(); var time = new ManualTime();
        await using var c = Make(rec, sink, time, TimeSpan.FromSeconds(1.5));
        c.ActivateFromHotkey(); await c.DrainAsync();
        rec.Say("s1", "how much space is on C", 0); await c.DrainAsync();
        time.Advance(TimeSpan.FromSeconds(1)); c.ForceTick(); await c.DrainAsync();
        Assert.Empty(sink.Commands); // still within the pause
        time.Advance(TimeSpan.FromSeconds(1)); c.ForceTick();
        await Settle(c, sink, 1);
        var cmd = Assert.Single(sink.Commands);
        Assert.Equal(InputMode.VoiceNatural, cmd.InputMode);
    }

    [Fact]
    public async Task BareExecuteConfirmsThePreviousNaturalRequest()
    {
        var rec = new FakeRecognizer(); var sink = new FakeSink(); var time = new ManualTime();
        await using var c = Make(rec, sink, time, TimeSpan.FromSeconds(1.5));
        c.ActivateFromHotkey(); await c.DrainAsync();
        rec.Say("s1", "organize my desktop", 0); await c.DrainAsync();
        time.Advance(TimeSpan.FromSeconds(2)); c.ForceTick();
        await Settle(c, sink, 1);
        for (var i = 0; i < 50 && c.State != AgentState.Standby; i++) { await Task.Delay(10); await c.DrainAsync(); }
        c.ActivateFromHotkey(); await c.DrainAsync();
        rec.Say("s2", "VoxStride execute", 0, conf: 0.95); await c.DrainAsync();
        await Settle(c, sink, 2);
        Assert.Equal(2, sink.Commands.Count);
        Assert.Equal(InputMode.Voice, sink.Commands[1].InputMode);
        Assert.Equal("organize my desktop", sink.Commands[1].FinalizedTranscript);
    }

    [Fact]
    public async Task WordsHeardWhileVoxStrideSpeaksAreIgnored()
    {
        var rec = new FakeRecognizer(); var sink = new FakeSink(); var time = new ManualTime();
        var speech = new SpeakingFake();
        await using var c = Make(rec, sink, time, null, speech);
        c.ActivateFromHotkey(); await c.DrainAsync();
        speech.IsSpeaking = true;
        rec.Say("s1", "this is the assistant's own voice", 0); await c.DrainAsync();
        speech.IsSpeaking = false;
        rec.Say("s2", "open notepad", 1000); await c.DrainAsync();
        Assert.Equal("open notepad", c.DisplayTranscript);
    }
}

public sealed class NaturalModeRunTests : IDisposable
{
    private readonly Harness _h = new();
    private readonly EventHub _hub = new();
    public void Dispose() => _h.Dispose();

    private RunCoordinator Make(IReasoningProvider? p) =>
        new(new IntentRouter(new FakeCatalog()), _h.Registry, new VoxStride.Core.Execution.ToolBroker(_h.Registry, _h.Authority, _h.Journal, _h.Approvals, new VoxStride.Core.Execution.NoStandingRules(), _hub, _h.Time),
            _h.Authority, new CountingSelector(p), _hub, null, null, new RunOptions { SpeakResults = false }, TimeProvider.System);

    private static CommandEnvelope Natural(string text) => new(Guid.NewGuid(), Guid.NewGuid(), text, VoxStride.Core.Execution.RunAuthority.HashTranscript(text), "en-US", [], DateTimeOffset.UtcNow, InputMode.VoiceNatural, null, null);

    [Fact]
    public async Task DirectChangeAsksForConfirmationInsteadOfActing()
    {
        File.WriteAllText(Path.Combine(_h.Desktop.Path, "a.pdf"), "x");
        var r = await Make(null).RunAsync(Natural("organize my desktop"), CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("VoxStride, execute", r.SpokenSummary, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "a.pdf")));
        Assert.Empty(_h.Approvals.Requests);
    }

    [Fact]
    public async Task DirectReadStillWorks()
    {
        var r = await Make(null).RunAsync(Natural("how much space is left on C"), CancellationToken.None);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task ModelCannotCallAMutatingToolInANaturalTurn()
    {
        File.WriteAllText(Path.Combine(_h.Desktop.Path, "a.pdf"), "x");
        var p = new ScriptedProvider(
            _ => [new ToolCallCompletedEvent("c1", "desktop_preview_organization", "{\"folder\":\"desktop\"}"), new CompletedEvent("tool_use")],
            _ => [new ToolCallCompletedEvent("c2", "desktop_apply_organization", "{\"manifestId\":\"m-000000000000\"}"), new CompletedEvent("tool_use")],
            _ => [new TextDeltaEvent("Say VoxStride, execute to confirm."), new CompletedEvent("end_turn")]);
        await Make(p).RunAsync(Natural("please tidy the desktop files into folders"), CancellationToken.None);
        Assert.Empty(_h.Approvals.Requests);
        Assert.Contains(p.ToolResults.SelectMany(x => x), o => o.Name == "desktop_apply_organization" && o.Result.ToJsonString().Contains("tool_not_allowed", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "a.pdf")));
    }
}

public sealed class PauseTests : IDisposable
{
    private readonly Harness _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task PausedRunStartsNoNewWriteUntilResumed()
    {
        for (var i = 0; i < 3; i++) File.WriteAllText(Path.Combine(_h.Desktop.Path, $"f{i}.pdf"), "x");
        var auth = _h.Mint();
        var preview = await _h.Call(auth, "desktop_preview_organization", new System.Text.Json.Nodes.JsonObject { ["folder"] = "desktop" });
        Assert.True(_h.Authority.Pause(auth.RunId));
        var manifestId = preview.ObservedState!["manifestId"]!.GetValue<string>();
        // Runs execute on a background thread in the app; a paused write blocks that thread, never the caller.
        var apply = Task.Run(() => _h.Call(auth, "desktop_apply_organization", new System.Text.Json.Nodes.JsonObject { ["manifestId"] = manifestId }));
        await Task.Delay(400);
        Assert.False(apply.IsCompleted);
        Assert.Equal(3, Directory.GetFiles(_h.Desktop.Path).Length); // nothing moved while paused
        _h.Authority.Resume(auth.RunId);
        var r = await apply;
        Assert.Equal(ToolOutcome.Success, r.Outcome);
    }

    [Fact]
    public async Task StopWhilePausedEndsWithoutChanges()
    {
        File.WriteAllText(Path.Combine(_h.Desktop.Path, "f.pdf"), "x");
        var auth = _h.Mint();
        var preview = await _h.Call(auth, "desktop_preview_organization", new System.Text.Json.Nodes.JsonObject { ["folder"] = "desktop" });
        _h.Authority.Pause(auth.RunId);
        var manifestId = preview.ObservedState!["manifestId"]!.GetValue<string>();
        // Runs execute on a background thread in the app; a paused write blocks that thread, never the caller.
        var apply = Task.Run(() => _h.Call(auth, "desktop_apply_organization", new System.Text.Json.Nodes.JsonObject { ["manifestId"] = manifestId }));
        await Task.Delay(200);
        _h.Authority.Revoke(auth.RunId);
        var r = await apply;
        Assert.NotEqual(ToolOutcome.Success, r.Outcome);
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "f.pdf")));
    }
}

public sealed class FailoverTests : IDisposable
{
    private readonly Harness _h = new();
    private readonly EventHub _hub = new();
    public void Dispose() => _h.Dispose();

    private sealed class Selector(IReasoningProvider main, IReasoningProvider? backup) : IProviderSelector
    {
        public IReasoningProvider? Current() => main;
        public IReasoningProvider? Fallback(ProviderKind failed) => backup;
    }

    private RunCoordinator Make(IProviderSelector sel) =>
        new(new IntentRouter(new FakeCatalog()), _h.Registry, new VoxStride.Core.Execution.ToolBroker(_h.Registry, _h.Authority, _h.Journal, _h.Approvals, new VoxStride.Core.Execution.NoStandingRules(), _hub, _h.Time),
            _h.Authority, sel, _hub, null, null, new RunOptions { SpeakResults = false }, TimeProvider.System);

    private static CommandEnvelope Cmd(string text) => new(Guid.NewGuid(), Guid.NewGuid(), text, VoxStride.Core.Execution.RunAuthority.HashTranscript(text), "en-US", [], DateTimeOffset.UtcNow, InputMode.Voice, null, null);

    [Fact]
    public async Task UnavailableProviderFailsOverBeforeAnyAction()
    {
        var down = new ScriptedProvider(_ => [new ProviderErrorEvent(ProviderErrorKind.Server, "overloaded", 529)]);
        var backup = new ScriptedProvider(_ => [new TextDeltaEvent("Here's your poem."), new CompletedEvent("end_turn")]);
        var r = await Make(new Selector(down, backup)).RunAsync(Cmd("write me a short poem about rain"), CancellationToken.None);
        Assert.True(r.Success);
        Assert.Equal("Here's your poem.", r.SpokenSummary);
        Assert.Equal(1, backup.Conversations);
    }

    [Fact]
    public async Task NoFailoverAfterAToolAlreadyRan()
    {
        var main = new ScriptedProvider(
            _ => [new ToolCallCompletedEvent("c1", "system_get_free_space", "{\"driveLetter\":\"C\"}"), new CompletedEvent("tool_use")],
            _ => [new ProviderErrorEvent(ProviderErrorKind.Server, "overloaded", 529)]);
        var backup = new ScriptedProvider(_ => [new TextDeltaEvent("x"), new CompletedEvent("end_turn")]);
        var r = await Make(new Selector(main, backup)).RunAsync(Cmd("check my drive and then write a poem about it"), CancellationToken.None);
        Assert.False(r.Success);
        Assert.Equal(0, backup.Conversations); // never restart a task that already did something
    }

    [Fact]
    public async Task RefusalIsNotAnOutage()
    {
        var main = new ScriptedProvider(_ => [new ProviderErrorEvent(ProviderErrorKind.Refused, "no")]);
        var backup = new ScriptedProvider(_ => [new TextDeltaEvent("x"), new CompletedEvent("end_turn")]);
        await Make(new Selector(main, backup)).RunAsync(Cmd("write something please"), CancellationToken.None);
        Assert.Equal(0, backup.Conversations);
    }
}
