using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using VoxStride.Contracts;
using VoxStride.Core.Voice;
using VoxStride.Speech;
using Xunit;
using Xunit.Abstractions;

namespace VoxStride.Tests.Windows;

/// <summary>
/// End-to-end regression for "VoxStride never hears me": synthetic speech is fed in real time, exactly like a
/// microphone, through <see cref="AudioHub"/> into the offline Windows recognizer, and the words must come out.
/// Before the fix the engine stopped after the first 50 ms buffer (SAPI reads a short read as end of audio).
/// Each test writes an "E2E-SPEECH" line saying whether it really ran, so CI logs show skipped prerequisites honestly.
/// </summary>
public sealed class SpeechPipelineTests(ITestOutputHelper output)
{
    /// <summary>Plays PCM in real time (50 ms buffers), then silence until stopped — behaves like a live microphone.</summary>
    private sealed class LiveFakeMicrophone(byte[] speech) : IAudioCaptureDevice
    {
        private volatile bool _running;
        private Thread? _thread;
        public string Label => "Synthetic test microphone";
        public int SampleRate => 16000;
        public event Action<byte[]>? DataAvailable;
        public event Action<string>? Error { add { } remove { } }
        public int Opens { get; private set; }

        public void Start()
        {
            Opens++;
            _running = true;
            _thread = new Thread(() =>
            {
                const int chunk = 1600; // 50 ms
                var silence = new byte[chunk];
                var pos = -16000 * 2; // 1 s of silence first
                var next = DateTime.UtcNow;
                while (_running)
                {
                    byte[] buf;
                    if (pos < 0 || pos >= speech.Length) buf = silence;
                    else { buf = new byte[chunk]; Array.Copy(speech, pos, buf, 0, Math.Min(chunk, speech.Length - pos)); }
                    pos += chunk;
                    DataAvailable?.Invoke(buf);
                    next = next.AddMilliseconds(50);
                    var wait = next - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Stop() { _running = false; _thread?.Join(1000); }
        public void Dispose() => Stop();
    }

    private static byte[]? Synthesize(string text)
    {
        if (SpeechEnvironment.Voices().Count == 0) return null;
        using var synth = new SpeechSynthesizer();
        using var ms = new MemoryStream();
        synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synth.Rate = -1;
        synth.Speak(text);
        return ms.ToArray();
    }

    private bool Prerequisites(out byte[] speech, string text)
    {
        speech = [];
        if (SpeechEnvironment.PickRecognizer("en-US") is null) { output.WriteLine("E2E-SPEECH: SKIPPED — no Windows speech recognizer installed on this machine"); return false; }
        var pcm = Synthesize(text);
        if (pcm is null || pcm.Length < 16000) { output.WriteLine("E2E-SPEECH: SKIPPED — no Windows voice available to synthesize test speech"); return false; }
        speech = pcm;
        return true;
    }

    private static async Task<string> ListenOnceAsync(WindowsSpeechRecognizer rec, int seconds)
    {
        var finals = new List<string>();
        void OnTranscript(TranscriptEvent e) { if (e.IsFinal && !string.IsNullOrWhiteSpace(e.Text)) lock (finals) finals.Add(e.Text); }
        rec.Transcript += OnTranscript;
        try
        {
            await rec.StartAsync(Guid.NewGuid(), CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await rec.StopAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        }
        finally { rec.Transcript -= OnTranscript; }
        lock (finals) return string.Join(" ", finals);
    }

    [Fact]
    public async Task OfflineRecognizerHearsLiveMicrophoneAudio()
    {
        const string sentence = "Please open the calculator and the notepad.";
        if (!Prerequisites(out var speech, sentence)) return;
        var mic = new LiveFakeMicrophone(speech);
        using var hub = new AudioHub(() => mic, output.WriteLine);
        var rec = new WindowsSpeechRecognizer("en-US", [], hub, output.WriteLine);
        try
        {
            var seconds = (int)Math.Ceiling(speech.Length / 32000.0) + 4;
            var heard = await ListenOnceAsync(rec, seconds);
            output.WriteLine($"E2E-SPEECH: ran — heard \"{heard}\"");
            Assert.False(string.IsNullOrWhiteSpace(heard), "the recognizer produced no words from live audio");
            Assert.Contains("open", heard, StringComparison.OrdinalIgnoreCase);
            Assert.False(hub.IsOpen, "the microphone must be released after listening stops");

            // A second session on the same recognizer must work too (engine and lease lifecycle).
            var again = await ListenOnceAsync(rec, seconds);
            output.WriteLine($"E2E-SPEECH: second session heard \"{again}\"");
            Assert.False(string.IsNullOrWhiteSpace(again), "the second listening session produced no words");
            Assert.False(hub.IsOpen);
        }
        finally { await rec.DisposeAsync(); }
    }

    [Fact]
    public async Task CancelledSessionReleasesTheMicrophone()
    {
        if (!Prerequisites(out var speech, "testing one two three")) return;
        var mic = new LiveFakeMicrophone(speech);
        using var hub = new AudioHub(() => mic, output.WriteLine);
        var rec = new WindowsSpeechRecognizer("en-US", [], hub, output.WriteLine);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Step(string what) => output.WriteLine($"E2E-SPEECH: {clock.ElapsedMilliseconds} ms — {what}");
        try
        {
            await rec.StartAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.True(hub.IsOpen);
            await Task.Delay(1500); // mid-speech
            Step("cancelling while speech is arriving");
            await rec.StopAsync(TimeSpan.Zero, CancellationToken.None); // cancel
            Step("cancelled");
            Assert.False(hub.IsOpen);
            await rec.StartAsync(Guid.NewGuid(), CancellationToken.None); // restarts cleanly after a cancel
            Step("restarted");
            Assert.True(hub.IsOpen);
            await Task.Delay(500);
            await rec.StopAsync(TimeSpan.Zero, CancellationToken.None);
            Step("cancelled again");
            Assert.False(hub.IsOpen);
        }
        finally { await rec.DisposeAsync(); }
        Step("disposed");
        // A cancel must be near-instant; a hang here would freeze Talk/Stop for the user.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"cancel/restart/dispose took {clock.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task WakeDetectorSharesAndReleasesTheMicrophone()
    {
        if (!Prerequisites(out var speech, "hello")) return;
        var mic = new LiveFakeMicrophone(speech);
        using var hub = new AudioHub(() => mic);
        var wake = new WindowsWakeWordDetector("en-US", hub);
        try
        {
            await wake.StartAsync(CancellationToken.None);
            Assert.True(hub.IsOpen);
            using (hub.Acquire("handoff"))
            {
                await wake.StopAsync();
                Assert.True(hub.IsOpen); // handover keeps the device open
            }
            Assert.False(hub.IsOpen);
            await wake.StartAsync(CancellationToken.None);
            await wake.StopAsync();
            Assert.False(hub.IsOpen);
            Assert.Equal(2, mic.Opens); // opened only while someone listens, never leaked
            output.WriteLine("E2E-SPEECH: ran — wake lifecycle");
        }
        finally { await wake.DisposeAsync(); }
    }
}
