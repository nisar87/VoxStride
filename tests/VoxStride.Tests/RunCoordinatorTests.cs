using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Agent;
using VoxStride.Core.Events;
using VoxStride.Core.Routing;
using VoxStride.Core.Tools;
using VoxStride.Providers;
using Xunit;

namespace VoxStride.Tests;

public sealed class CountingSelector(IReasoningProvider? p) : IProviderSelector
{
    public int Calls { get; private set; }
    public IReasoningProvider? Current() { Calls++; return p; }
}

/// <summary>Scripted provider: each round yields the configured events (after an optional delay).</summary>
public sealed class ScriptedProvider(params Func<int, IEnumerable<ProviderEvent>>[] rounds) : IReasoningProvider
{
    internal readonly Func<int, IEnumerable<ProviderEvent>>[] Rounds = rounds;
    public ProviderKind Kind => ProviderKind.Claude;
    public string Model => "scripted";
    public ProviderCapabilities Capabilities => new(true, true, false, 64);
    public TimeSpan Delay { get; set; }
    public int Conversations { get; private set; }
    public List<IReadOnlyList<ToolCallOutput>> ToolResults { get; } = [];
    public IProviderConversation StartConversation(string systemInstruction, IReadOnlyList<ToolDefinition> tools) { Conversations++; return new Conv(this); }
    public Task<ProviderCheckResult> CheckAsync(CancellationToken ct) => Task.FromResult(new ProviderCheckResult(true, "ok"));

    private sealed class Conv(ScriptedProvider p) : IProviderConversation
    {
        private int _round;
        public void AddUserText(string text) { }
        public void AddToolResults(IReadOnlyList<ToolCallOutput> outputs) => p.ToolResults.Add(outputs);
        public async IAsyncEnumerable<ProviderEvent> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            if (p.Delay > TimeSpan.Zero) await Task.Delay(p.Delay, ct);
            var r = _round++;
            foreach (var e in p.Rounds[Math.Min(r, p.Rounds.Length - 1)](r)) yield return e;
        }
    }
}

public sealed class RunCoordinatorTests : IDisposable
{
    private readonly Harness _h = new();
    private readonly EventHub _hub = new();
    private readonly List<TaskEvent> _events = [];

    public RunCoordinatorTests() { _hub.Published += e => { lock (_events) _events.Add(e); }; }
    public void Dispose() => _h.Dispose();

    private RunCoordinator Make(IProviderSelector sel, RunOptions? o = null) =>
        new(new IntentRouter(new FakeCatalog()), _h.Registry, new VoxStride.Core.Execution.ToolBroker(_h.Registry, _h.Authority, _h.Journal, _h.Approvals, new VoxStride.Core.Execution.NoStandingRules(), _hub, _h.Time),
            _h.Authority, sel, _hub, null, null, o ?? new RunOptions { SpeakResults = false }, TimeProvider.System);

    private CommandEnvelope Cmd(string text) => new(Guid.NewGuid(), Guid.NewGuid(), text, VoxStride.Core.Execution.RunAuthority.HashTranscript(text), "en-US", [], DateTimeOffset.UtcNow, InputMode.Voice, null, null);

    [Fact]
    public async Task L01_DirectDiskReadMakesNoProviderCallAndIsFast()
    {
        var provider = new ScriptedProvider(_ => throw new InvalidOperationException("must not be called"));
        var sel = new CountingSelector(provider);
        var rc = Make(sel);
        var times = new List<double>();
        for (var i = 0; i < 100; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = await rc.RunAsync(Cmd("how much space is left on C"), CancellationToken.None);
            times.Add(sw.Elapsed.TotalMilliseconds);
            Assert.True(r.Success);
            Assert.Contains("123.46 GB", r.SpokenSummary);
        }
        Assert.Equal(0, sel.Calls);
        Assert.Equal(0, provider.Conversations);
        times.Sort();
        var p95 = times[94];
        Assert.True(p95 < 500, $"p95={p95}ms");
        Assert.NotNull(rc.LastLatency!.Between("t_commit", "t_first_verified_result"));
    }

    [Fact]
    public async Task DirectOrganizeHonorsExclusionAndAsksApproval()
    {
        _h.Desktop.File("a.pdf"); _h.Desktop.File("b.png"); _h.Desktop.File("c.zip"); _h.Desktop.File("Link.lnk");
        var rc = Make(new CountingSelector(null));
        var r = await rc.RunAsync(Cmd("organize my desktop and leave zip files alone"), CancellationToken.None);
        Assert.True(r.Success, r.SpokenSummary);
        Assert.Single(_h.Approvals.Requests);
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "c.zip")));
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "Link.lnk")));
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "PDFs", "a.pdf")));
        Assert.Contains("Moved 2 of 2", r.SpokenSummary);
    }

    [Fact]
    public async Task CloudPathExecutesVerifiedToolAndUsesNarrative()
    {
        var p = new ScriptedProvider(
            _ => [new ToolCallStartedEvent("c1", "system_get_free_space"), new ToolCallCompletedEvent("c1", "system_get_free_space", "{\"driveLetter\":\"C\"}"), new UsageEvent(100, 10), new CompletedEvent("tool_use")],
            _ => [new TextDeltaEvent("Drive C has about 123 GB free."), new UsageEvent(120, 12), new CompletedEvent("end_turn")]);
        var rc = Make(new CountingSelector(p));
        var r = await rc.RunAsync(Cmd("roughly how roomy is my main disk, in plain words"), CancellationToken.None);
        Assert.True(r.Success);
        Assert.Equal("Drive C has about 123 GB free.", r.SpokenSummary);
        Assert.Equal("success", p.ToolResults.Single().Single().Result["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task ModelClaimingSuccessForDeclinedActionIsOverriddenByEvidence()
    {
        _h.Desktop.File("a.pdf");
        _h.Approvals.Approve = false;
        ScriptedProvider? p = null;
        p = new ScriptedProvider(
            _ => [new ToolCallCompletedEvent("c1", "desktop_preview_organization", "{\"folder\":\"desktop\"}"), new CompletedEvent("tool_use")],
            _ =>
            {
                var id = p!.ToolResults[0][0].Result["state"]!["manifestId"]!.GetValue<string>();
                return [new ToolCallCompletedEvent("c2", "desktop_apply_organization", $"{{\"manifestId\":\"{id}\"}}"), new CompletedEvent("tool_use")];
            },
            _ => [new TextDeltaEvent("All done! Your desktop is perfectly organized."), new CompletedEvent("end_turn")]);
        var rc = Make(new CountingSelector(p));
        var r = await rc.RunAsync(Cmd("group my desktop files sensibly please"), CancellationToken.None);
        Assert.False(r.Success);
        Assert.DoesNotContain("perfectly organized", r.SpokenSummary);
        Assert.Contains("declined", r.SpokenSummary);
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "a.pdf")));
    }

    [Fact]
    public async Task MalformedToolArgumentsAreRejectedNotExecuted()
    {
        var p = new ScriptedProvider(
            _ => [new ToolCallCompletedEvent("c1", "system_get_free_space", "{\"driveLetter\": \"C"), new CompletedEvent("tool_use")],
            _ => [new TextDeltaEvent("Sorry."), new CompletedEvent("end_turn")]);
        var rc = Make(new CountingSelector(p));
        await rc.RunAsync(Cmd("tell me about my disk in a fancy way"), CancellationToken.None);
        Assert.Equal("malformed_arguments", p.ToolResults.Single().Single().Result["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task L03_SlowProviderShowsWaitingStatusAndCancelPreventsLateActions()
    {
        _h.Desktop.File("a.pdf");
        var p = new ScriptedProvider(_ => [new ToolCallCompletedEvent("c1", "desktop_preview_organization", "{\"folder\":\"desktop\"}"), new CompletedEvent("tool_use")])
        { Delay = TimeSpan.FromSeconds(3) };
        var rc = Make(new CountingSelector(p), new RunOptions { SpeakResults = false, WaitingNoticeInterval = TimeSpan.FromMilliseconds(200) });
        var run = rc.RunAsync(Cmd("make my desktop tidy somehow"), CancellationToken.None);
        await Task.Delay(700);
        Assert.Contains(_events, e => e.Phase == "waiting");
        await rc.CancelActiveAsync("test");
        var r = await run;
        Assert.False(r.Success);
        Assert.Contains("Nothing was changed", r.SpokenSummary);
        Assert.DoesNotContain(_events, e => e.Phase == "step");
    }

    [Fact]
    public async Task P02_ProviderErrorGivesActionableMessageAndDirectToolsStillWork()
    {
        var p = new ScriptedProvider(_ => [new ProviderErrorEvent(ProviderErrorKind.Unauthorized, "bad key", 401)]);
        var rc = Make(new CountingSelector(p));
        var r = await rc.RunAsync(Cmd("write me a haiku about rain in notepad"), CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("rejected the API key", r.SpokenSummary);
        var d = await rc.RunAsync(Cmd("check C drive"), CancellationToken.None);
        Assert.True(d.Success);
    }

    [Fact]
    public async Task NoProviderConfiguredExplainsWhatStillWorks()
    {
        var rc = Make(new CountingSelector(null));
        var r = await rc.RunAsync(Cmd("write a poem about Lahore"), CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("none is configured", r.SpokenSummary);
    }

    [Fact]
    public async Task StepLimitIsEnforced()
    {
        var p = new ScriptedProvider(r => [new ToolCallCompletedEvent("c" + r, "system_get_free_space", "{\"driveLetter\":\"C\"}"), new CompletedEvent("tool_use")]);
        var rc = Make(new CountingSelector(p), new RunOptions { SpeakResults = false, MaxSteps = 5 });
        var r = await rc.RunAsync(Cmd("keep checking my disk forever"), CancellationToken.None);
        Assert.False(r.Success);
        Assert.True(p.ToolResults.Count <= 5);
    }

    [Fact]
    public async Task B03_InjectedInstructionsInToolOutputCannotUseToolsOutsideRegistry()
    {
        var p = new ScriptedProvider(
            _ => [new ToolCallCompletedEvent("c1", "shell_execute", "{\"cmd\":\"del /s C:\\\\\"}"), new ToolCallCompletedEvent("c2", "browser_upload_file", "{}"), new CompletedEvent("tool_use")],
            _ => [new TextDeltaEvent("I could not do that."), new CompletedEvent("end_turn")]);
        var rc = Make(new CountingSelector(p));
        await rc.RunAsync(Cmd("summarize this web page for me"), CancellationToken.None);
        Assert.All(p.ToolResults.Single(), o => Assert.Equal("unknown_tool", o.Result["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ClarifyRouteAsksWithoutAction()
    {
        var rc = Make(new CountingSelector(null));
        var r = await rc.RunAsync(Cmd("open visual"), CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("Which app", r.SpokenSummary);
    }
}

public sealed class VideoLayoutTests
{
    private static VideoCard C(string t, double x, double y, bool ad = false, bool shorts = false) => new(t, $"https://www.youtube.com/watch?v={t}", x, y, 300, 250, ad, shorts);

    [Fact]
    public void B01_ThirdInTopRowSkipsAdsAndUsesVisualOrder()
    {
        var cards = new[] { C("ad", 10, 100, ad: true), C("v2", 330, 102), C("v1", 10, 400), C("v3", 650, 99), C("v4", 970, 101), C("v0", 10, 98) };
        var sel = VideoLayout.Select(cards, 1, 3);
        Assert.Equal("v3", sel.Selected!.Title);
    }

    [Fact]
    public void FewerThanRequestedInRowReturnsChoicesInsteadOfGuessing()
    {
        var cards = new[] { C("a", 10, 100), C("b", 330, 100), C("c", 10, 400), C("d", 330, 400) };
        var sel = VideoLayout.Select(cards, 1, 3);
        Assert.Null(sel.Selected);
        Assert.Contains("only 2", sel.Ambiguity);
    }

    [Fact]
    public void SingleColumnListUsesOverallOrderOnlyForTopRow()
    {
        var cards = new[] { C("a", 10, 100), C("b", 10, 400), C("c", 10, 700) };
        Assert.Equal("c", VideoLayout.Select(cards, 1, 3).Selected!.Title);
        Assert.Null(VideoLayout.Select(cards, 2, 1).Selected);
    }

    [Fact]
    public void ShortsExcludedByDefault()
    {
        var cards = new[] { C("s", 10, 100, shorts: true), C("a", 330, 100), C("b", 650, 100) };
        Assert.Equal("b", VideoLayout.Select(cards, 1, 2).Selected!.Title);
    }

    [Fact]
    public void VideoIdParsing() => Assert.Equal("abc123", YouTubePlayTool.VideoId("https://www.youtube.com/watch?v=abc123&t=5"));
}

public sealed class SchemaValidatorTests
{
    [Fact]
    public void ValidatesTypesEnumsRangesAndArrays()
    {
        var s = VoxStride.Core.Json.Schema.Object(
            ("mode", VoxStride.Core.Json.Schema.Str("m", null, 10, "a", "b"), true),
            ("n", VoxStride.Core.Json.Schema.Int("n", 1, 5), false),
            ("list", VoxStride.Core.Json.Schema.Array("l", VoxStride.Core.Json.Schema.Str("x", "^[a-z]+$", 5), 2), false));
        Assert.Empty(VoxStride.Core.Json.SchemaValidator.Validate(new JsonObject { ["mode"] = "a", ["n"] = 3, ["list"] = new JsonArray("ab") }, s));
        Assert.NotEmpty(VoxStride.Core.Json.SchemaValidator.Validate(new JsonObject { ["mode"] = "c" }, s));
        Assert.NotEmpty(VoxStride.Core.Json.SchemaValidator.Validate(new JsonObject { ["mode"] = "a", ["n"] = 9 }, s));
        Assert.NotEmpty(VoxStride.Core.Json.SchemaValidator.Validate(new JsonObject { ["mode"] = "a", ["n"] = 2.5 }, s));
        Assert.NotEmpty(VoxStride.Core.Json.SchemaValidator.Validate(new JsonObject { ["mode"] = "a", ["list"] = new JsonArray("a", "b", "c") }, s));
        Assert.NotEmpty(VoxStride.Core.Json.SchemaValidator.Validate(new JsonObject { ["mode"] = "a", ["list"] = new JsonArray("A1") }, s));
        Assert.NotEmpty(VoxStride.Core.Json.SchemaValidator.Validate(new JsonObject { ["mode"] = 1 }, s));
        Assert.NotEmpty(VoxStride.Core.Json.SchemaValidator.Validate(new JsonArray(), s));
    }

    [Fact]
    public void DeeplyNestedArgumentsAreRejected()
    {
        var deep = new string('[', 40) + new string(']', 40);
        Assert.False(VoxStride.Core.Json.SchemaValidator.TryParseArguments("{\"a\":" + deep + "}", out _, out _));
    }
}
