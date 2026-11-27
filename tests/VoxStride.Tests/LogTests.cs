using VoxStride.Contracts;
using Xunit;

namespace VoxStride.Tests;

/// <summary>Captures log entries; tests that swap the global sink run in one collection so they don't interleave.</summary>
public sealed class CapturingSink : ILogSink
{
    private readonly List<LogEntry> _entries = [];
    public void Write(in LogEntry entry) { lock (_entries) _entries.Add(entry); }
    public IReadOnlyList<LogEntry> Entries { get { lock (_entries) return [.. _entries]; } }
    public bool Has(string area, string contains) => Entries.Any(e => e.Area == area && e.Message.Contains(contains, StringComparison.Ordinal));

    /// <summary>Installs this sink for the duration of a test.</summary>
    public static Scope Install(out CapturingSink sink) { sink = new CapturingSink(); return new Scope(sink); }

    public sealed class Scope : IDisposable
    {
        private readonly ILogSink? _previous = Log.Sink;
        private readonly bool _content = Log.IncludeContent;
        internal Scope(ILogSink sink) => Log.Sink = sink;
        public void Dispose() { Log.Sink = _previous; Log.IncludeContent = _content; }
    }
}

[Collection("GlobalLog")]
public sealed class LogTests
{
    // Fake keys are assembled at runtime so no key-shaped literal exists in the repository (the CI secret scan stays strict).
    private static readonly string Fake = "AbCdEfGh" + "IjKlMnOpQrSt";

    [Theory]
    [InlineData("key {ant}api03-{fake} rejected", "sk-ant-***")]
    [InlineData("openai {proj}{fake} failed", "sk-***")]
    [InlineData("GET /v1beta/models?key={goog}{fake}12345678901234567", "key=***")]
    [InlineData("google key {goog}{fake}12345678901234567 invalid", "AIza***")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.{fake}", "Bearer ***")]
    [InlineData("url ?key={fake}&alt=sse", "key=***")]
    [InlineData("{\"api_key\":\"{fake}\"}", "***")]
    [InlineData("x-api-key: {ant}{fake}", "***")]
    public void RedactsSecrets(string template, string expected)
    {
        var input = template.Replace("{ant}", "sk-" + "ant-", StringComparison.Ordinal).Replace("{proj}", "sk-" + "proj-", StringComparison.Ordinal)
            .Replace("{goog}", "AI" + "zaSy", StringComparison.Ordinal).Replace("{fake}", Fake, StringComparison.Ordinal);
        var r = Log.Redact(input);
        Assert.Contains(expected, r, StringComparison.Ordinal);
        Assert.DoesNotContain(Fake, r, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesOrdinaryTextAlone()
    {
        const string s = "speech-session engine=offline device=\"HyperX\" buffers=163 peak=6903 task=sk-ip";
        Assert.Equal(s, Log.Redact(s));
    }

    [Fact]
    public void MessagesAndExceptionsAreRedactedBeforeReachingTheSink()
    {
        using var scope = CapturingSink.Install(out var sink);
        Log.Error("redaction-test", "call failed with key " + "sk-" + "ant-api03-SECRETSECRET", new InvalidOperationException("Bearer abcdefghijklmnop"));
        // Other test classes run in parallel and may log too; pick out this test's line.
        var e = Assert.Single(sink.Entries, x => x.Area == "redaction-test");
        Assert.Equal(LogLevel.Error, e.Level);
        Assert.DoesNotContain("SECRETSECRET", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", e.Exception!, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", e.Exception!, StringComparison.Ordinal);
    }

    [Fact]
    public void TextHonoursContentSetting()
    {
        using var scope = CapturingSink.Install(out var unused);
        Log.IncludeContent = true;
        Assert.Equal("\"open notepad\"", Log.Text(" open notepad "));
        Log.IncludeContent = false;
        Assert.Equal("<12 chars hidden>", Log.Text("open notepad"));
        Assert.Equal("(none)", Log.Text(null));
    }

    [Fact]
    public void LongTextIsTrimmed()
    {
        using var scope = CapturingSink.Install(out var unused);
        Log.IncludeContent = true;
        var t = Log.Text(new string('a', 500), 10);
        Assert.StartsWith("\"aaaaaaaaaa…(+490)", t, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenSinkNeverThrows()
    {
        var previous = Log.Sink;
        Log.Sink = new ThrowingSink();
        try { Log.Info("app", "still fine"); }
        finally { Log.Sink = previous; }
    }

    [Fact]
    public void TimingLogsElapsed()
    {
        using var scope = CapturingSink.Install(out var sink);
        using (Log.Time("timing-test", "route decision", LogLevel.Info)) { }
        Assert.True(sink.Has("timing-test", "route decision took"));
    }

    private sealed class ThrowingSink : ILogSink { public void Write(in LogEntry entry) => throw new IOException("disk full"); }
}

[Collection("GlobalLog")]
public sealed class DiagnosticsTrailTests
{
    [Fact]
    public async Task SpokenCommandLeavesACompleteTrail()
    {
        using var scope = CapturingSink.Install(out var log);
        Log.IncludeContent = true;
        var rec = new FakeRecognizer();
        var sink = new FakeSink();
        await using (var c = new VoxStride.Core.Voice.ConversationCoordinator(rec, null, sink, new VoxStride.Core.Voice.ControlPhraseDetector(new VoxStride.Core.Voice.ControlPhraseOptions()), new VoxStride.Core.Voice.CoordinatorOptions(), new ManualTime()))
        {
            c.ActivateFromHotkey();
            await c.DrainAsync();
            rec.Say("s1", "open notepad", 0);
            await c.DrainAsync();
            c.PressProceed();
            for (var i = 0; i < 50 && sink.Commands.Count == 0; i++) { await Task.Delay(20); await c.DrainAsync(); }
        }
        Assert.True(log.Has("voice", "input activate source=hotkey"));
        Assert.True(log.Has("voice", "state Standby -> Listening (activated by hotkey)"));
        Assert.True(log.Has("voice", "heard final seg=s1"));
        Assert.True(log.Has("voice", "\"open notepad\""));
        Assert.True(log.Has("voice", "input proceed"));
        Assert.True(log.Has("voice", "recognizer flushed in"));
        Assert.True(log.Has("voice", "commit mode=Voice"));
    }

    [Fact]
    public async Task ContentIsHiddenWhenTheUserTurnsItOff()
    {
        using var scope = CapturingSink.Install(out var log);
        Log.IncludeContent = false;
        var rec = new FakeRecognizer();
        await using (var c = new VoxStride.Core.Voice.ConversationCoordinator(rec, null, new FakeSink(), new VoxStride.Core.Voice.ControlPhraseDetector(new VoxStride.Core.Voice.ControlPhraseOptions()), new VoxStride.Core.Voice.CoordinatorOptions(), new ManualTime()))
        {
            c.ActivateFromHotkey();
            await c.DrainAsync();
            rec.Say("s1", "my secret plan", 0);
            await c.DrainAsync();
        }
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("secret plan", StringComparison.Ordinal));
        Assert.True(log.Has("voice", "chars hidden"));
    }

    [Fact]
    public async Task HttpLogNeverContainsQueryKeys()
    {
        using var scope = CapturingSink.Install(out var log);
        using var http = new HttpClient(new VoxStride.Providers.LoggingHandler(new StubHandler()));
        using var resp = await http.GetAsync(new Uri("https://generativelanguage.googleapis.com/v1beta/models/x:stream?key=" + "AI" + "zaSyVerySecretValue123456&alt=sse"));
        var line = Assert.Single(log.Entries, e => e.Area == "http" && e.Message.Contains("x:stream", StringComparison.Ordinal));
        Assert.Contains("generativelanguage.googleapis.com/v1beta/models/x:stream -> 200", line.Message, StringComparison.Ordinal);
        Assert.Contains("requestId=req-42", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("VerySecret", line.Message, StringComparison.Ordinal);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var r = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            r.Headers.Add("request-id", "req-42");
            return Task.FromResult(r);
        }
    }
}
