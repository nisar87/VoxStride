using System.Runtime.InteropServices;
using System.Threading.Channels;
using VoxStride.Contracts;
using VoxStride.Core.Voice;
using VoxStride.Providers;

namespace VoxStride.Speech;

/// <summary>
/// Cloud speech recognition for accented and mixed-language speech: records only while VoxStride is listening,
/// splits speech at pauses, and transcribes each utterance with OpenAI or Gemini. Shows "…" while an utterance is
/// being transcribed (a pending segment, so nothing can commit early). Never runs in standby.
/// </summary>
public sealed class CloudSpeechRecognizer : ISpeechRecognizer, ISpeechDiagnostics
{
    private readonly ITranscriber _transcriber;
    private readonly AudioHub _mic;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private Channel<(AudioSegment seg, string id, Guid session)>? _queue;
    private Task? _worker;
    private UtteranceSegmenter? _segmenter;
    private AudioLease? _lease;
    private MicWatchdog? _watchdog;
    private Guid _session;
    private long _sequence;
    private int _counter;
    private string? _openId;
    private string _context = "";
    private int _transcribed;

    public CloudSpeechRecognizer(ITranscriber transcriber, AudioHub microphone, double sensitivity = 3.0, Action<string>? log = null)
    {
        _transcriber = transcriber;
        _mic = microphone;
        _log = log ?? (m => Log.Info("speech", m));
        Sensitivity = sensitivity;
        _mic.DeviceError += m => { if (_lease is not null) Error?.Invoke(m); };
    }

    public double Sensitivity { get; }
    public string Name => "Cloud speech (" + _transcriber.Name + ")";
    public event Action<TranscriptEvent>? Transcript;
    public event Action<string>? Error;
    public event Action<string>? Hint;

    public Task StartAsync(Guid sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            _session = sessionId;
            if (_lease is not null) return Task.CompletedTask; // already listening; session switched
            var lease = _mic.Acquire("cloud-dictation"); // throws a clear message if the microphone can't be opened
            _context = "";
            _transcribed = 0;
            var segmenter = new UtteranceSegmenter(lease.SampleRate) { Sensitivity = Sensitivity };
            segmenter.SpeechStarted += startMs =>
            {
                var id = "c-" + Interlocked.Increment(ref _counter);
                _openId = id;
                Raise(new TranscriptEvent(_session, id, Interlocked.Increment(ref _sequence), 1, false, "…", startMs, startMs));
            };
            segmenter.SpeechDiscarded += () =>
            {
                if (_openId is { } id) Raise(new TranscriptEvent(_session, id, Interlocked.Increment(ref _sequence), 2, true, "", 0, 0));
                _openId = null;
            };
            segmenter.SegmentReady += seg =>
            {
                var id = _openId ?? "c-" + Interlocked.Increment(ref _counter);
                _openId = null;
                _queue?.Writer.TryWrite((seg, id, _session));
            };
            _segmenter = segmenter;
            _queue = Channel.CreateUnbounded<(AudioSegment, string, Guid)>(new UnboundedChannelOptions { SingleReader = true });
            _worker = Task.Run(() => WorkAsync(_queue.Reader));
            lease.Data += d => { lock (_gate) _segmenter?.Push(d); };
            _lease = lease;
            _watchdog = new MicWatchdog(lease, m => Error?.Invoke(m), m => Hint?.Invoke(m));
            _log?.Invoke($"speech-start engine=cloud ({_transcriber.Name}) device=\"{lease.DeviceLabel}\"");
        }
        return Task.CompletedTask;
    }

    private async Task WorkAsync(ChannelReader<(AudioSegment seg, string id, Guid session)> reader)
    {
        await foreach (var (seg, id, session) in reader.ReadAllAsync().ConfigureAwait(false))
        {
            var wav = Wav.FromPcm16(seg.Pcm16, seg.SampleRate);
            TranscriptionResult r;
            try { r = await _transcriber.TranscribeAsync(wav, _context, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { r = new TranscriptionResult(false, "", ex.Message); }
            if (!r.Ok) { _log?.Invoke("transcribe-failed: " + r.Error); Error?.Invoke("I couldn't transcribe that part (" + r.Error + ")"); }
            var text = r.Text;
            if (text.Length > 0) { _context = (_context + " " + text).Trim(); Interlocked.Increment(ref _transcribed); }
            Raise(new TranscriptEvent(session, id, Interlocked.Increment(ref _sequence), 2, true, text, seg.StartMs, seg.EndMs, r.Ok ? 0.95 : 0));
        }
    }

    private void Raise(TranscriptEvent e) => Transcript?.Invoke(e);

    public async Task StopAsync(TimeSpan flushTimeout, CancellationToken ct)
    {
        Channel<(AudioSegment, string, Guid)>? q;
        Task? worker;
        AudioLease? lease;
        lock (_gate)
        {
            lease = _lease;
            if (lease is null) return;
            _lease = null;
            _watchdog?.Dispose();
            _watchdog = null;
            if (flushTimeout > TimeSpan.Zero) _segmenter?.Flush();
            _segmenter = null;
            q = _queue; worker = _worker;
            _queue = null; _worker = null;
        }
        lease.Dispose(); // microphone released before waiting on the network
        q?.Writer.TryComplete();
        if (worker is not null && flushTimeout > TimeSpan.Zero)
        {
            try { await Task.WhenAny(worker, Task.Delay(flushTimeout, ct)).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _log?.Invoke($"speech-session engine=cloud device=\"{lease.DeviceLabel}\" ms={(long)lease.Elapsed.TotalMilliseconds} buffers={lease.Buffers} peak={lease.Peak:0} transcribed={_transcribed}");
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
    }
}

/// <summary>
/// Speaks with a natural cloud voice when available, falling back to the local Windows voice. Cues stay local.
/// Playback runs asynchronously from unmanaged memory and is cancelled instantly when the user interrupts.
/// </summary>
public sealed class NaturalSpeechSynthesizer(WindowsSpeechSynthesizer local, ICloudVoice? cloud) : ISpeechSynthesizer, IDisposable
{
    private readonly Lock _gate = new();
    private nint _playing;
    private CancellationTokenSource? _current;
    public ICloudVoice? Cloud { get; set; } = cloud;
    public bool Enabled { get => local.Enabled; set => local.Enabled = value; }
    public bool CuesEnabled { get => local.CuesEnabled; set => local.CuesEnabled = value; }
    public bool IsSpeaking => local.IsSpeaking || _playing != 0;

    /// <summary>When the last spoken reply ended (used to ignore room echo of VoxStride's own voice).</summary>
    public DateTimeOffset LastSpeechEnded { get; private set; } = DateTimeOffset.MinValue;

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        try { await SpeakCoreAsync(text, ct).ConfigureAwait(false); }
        finally { LastSpeechEnded = DateTimeOffset.UtcNow; }
    }

    private async Task SpeakCoreAsync(string text, CancellationToken ct)
    {
        if (!local.Enabled || string.IsNullOrWhiteSpace(text)) return;
        StopSpeaking();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _current = cts;
        byte[]? wav = null;
        if (Cloud is { } c)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try { wav = await c.SynthesizeAsync(text, timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (cts.IsCancellationRequested) { Log.Debug("tts", "speech interrupted before playback"); return; }
        if (wav is null || wav.Length < 100)
        {
            if (Cloud is not null) Log.Info("tts", $"natural voice unavailable after {sw.ElapsedMilliseconds} ms; using the Windows voice");
            await local.SpeakAsync(text, cts.Token).ConfigureAwait(false);
            return;
        }
        Log.Info("tts", $"natural voice ready in {sw.ElapsedMilliseconds} ms, playing {(wav.Length - 44) / 48000.0:0.0}s");
        var p = Marshal.AllocHGlobal(wav.Length);
        Marshal.Copy(wav, 0, p, wav.Length);
        lock (_gate) { FreePlaying(); _playing = p; }
        PlaySound(p, 0, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
        var seconds = Math.Max(1, (wav.Length - 44) / (24000.0 * 2)); // gpt-4o-mini-tts WAV is 24 kHz mono 16-bit
        try { await Task.Delay(TimeSpan.FromSeconds(seconds + 0.5), cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        lock (_gate) { if (_playing == p) { PlaySound(0, 0, 0); FreePlaying(); } }
    }

    private void FreePlaying()
    {
        if (_playing == 0) return;
        Marshal.FreeHGlobal(_playing);
        _playing = 0;
    }

    public void StopSpeaking()
    {
        lock (_gate)
        {
            _current?.Cancel();
            if (_playing != 0) { PlaySound(0, 0, 0); FreePlaying(); }
        }
        local.StopSpeaking();
    }

    public void PlayCue(CueKind cue) => local.PlayCue(cue);

    public void Dispose() { StopSpeaking(); local.Dispose(); }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySound(nint sound, nint hmod, uint flags);
    private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;
}
