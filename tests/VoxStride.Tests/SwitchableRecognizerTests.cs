using VoxStride.Contracts;
using VoxStride.Core.Voice;
using Xunit;

namespace VoxStride.Tests;

public sealed class SwitchableRecognizerTests
{
    private sealed class Fake(string name) : ISpeechRecognizer, ISpeechDiagnostics
    {
        public string Name => name;
        public bool Started, Disposed;
        public TimeSpan LastFlush;
        public event Action<TranscriptEvent>? Transcript;
        public event Action<string>? Error;
        public event Action<string>? Hint;
        public Task StartAsync(Guid sessionId, CancellationToken ct) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(TimeSpan flushTimeout, CancellationToken ct) { LastFlush = flushTimeout; Started = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public void Say(string text) => Transcript?.Invoke(new TranscriptEvent(Guid.Empty, "s", 1, 1, true, text, 0, 0));
        public void Warn(string m) { Error?.Invoke(m); Hint?.Invoke(m); }
    }

    [Fact]
    public async Task IdleSwapIsImmediateAndOldEngineIsSilenced()
    {
        var offline = new Fake("offline"); var gemini = new Fake("gemini");
        var r = new SwitchableRecognizer(offline);
        var heard = new List<string>(); var switched = new List<string>();
        r.Transcript += e => heard.Add(e.Text);
        r.Switched += switched.Add;
        await r.ReplaceAsync(gemini);
        offline.Say("old");
        gemini.Say("new");
        Assert.True(offline.Disposed);
        Assert.Equal("gemini", r.Name);
        Assert.True(heard.SequenceEqual(["new"]));
        Assert.True(switched.SequenceEqual(["gemini"]));
    }

    [Fact]
    public async Task SwapDuringDictationWaitsUntilTheSessionEnds()
    {
        var offline = new Fake("offline"); var gemini = new Fake("gemini");
        var r = new SwitchableRecognizer(offline);
        var heard = new List<string>();
        r.Transcript += e => heard.Add(e.Text);
        await r.StartAsync(Guid.NewGuid(), CancellationToken.None);
        await r.ReplaceAsync(gemini);
        offline.Say("still the same instruction");  // the instruction in progress is not cut
        Assert.False(offline.Disposed);
        Assert.Equal("offline", r.Name);
        await r.StopAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
        Assert.True(offline.Disposed);
        Assert.Equal("gemini", r.Name);
        await r.StartAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.True(gemini.Started);
        Assert.True(heard.SequenceEqual(["still the same instruction"]));
    }

    [Fact]
    public async Task FlushWaitIsCappedPerEngine()
    {
        var offline = new Fake("offline");
        var r = new SwitchableRecognizer(offline, e => e.Name == "offline" ? TimeSpan.FromMilliseconds(1500) : TimeSpan.FromSeconds(8));
        await r.StartAsync(Guid.NewGuid(), CancellationToken.None);
        await r.StopAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), offline.LastFlush);
    }

    [Fact]
    public async Task ErrorsAndHintsAreForwardedFromTheCurrentEngineOnly()
    {
        var a = new Fake("a"); var b = new Fake("b");
        var r = new SwitchableRecognizer(a);
        var errors = new List<string>(); var hints = new List<string>();
        r.Error += errors.Add; r.Hint += hints.Add;
        a.Warn("from a");
        await r.ReplaceAsync(b);
        a.Warn("late from a");
        b.Warn("from b");
        Assert.True(errors.SequenceEqual(["from a", "from b"]));
        Assert.True(hints.SequenceEqual(["from a", "from b"]));
    }

    [Fact]
    public async Task ALaterReplacementSupersedesAPendingOne()
    {
        var a = new Fake("a"); var b = new Fake("b"); var c = new Fake("c");
        var r = new SwitchableRecognizer(a);
        await r.StartAsync(Guid.NewGuid(), CancellationToken.None);
        await r.ReplaceAsync(b);
        await r.ReplaceAsync(c);
        Assert.True(b.Disposed);
        await r.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal("c", r.Name);
    }
}
