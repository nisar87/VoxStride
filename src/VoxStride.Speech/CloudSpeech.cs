using System.Runtime.InteropServices;
using System.Threading.Channels;
using VoxStride.Contracts;
using VoxStride.Core.Voice;
using VoxStride.Providers;

namespace VoxStride.Speech;

/// <summary>Minimal WASAPI-compatible microphone capture through winmm waveIn (16 kHz, mono, 16-bit). No extra dependencies.</summary>
public sealed class WaveInRecorder : IDisposable
{
    private const int WAVE_MAPPER = -1, CALLBACK_EVENT = 0x50000, WHDR_DONE = 1, BufferMs = 50, BufferCount = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR { public nint lpData; public uint dwBufferLength, dwBytesRecorded; public nint dwUser; public uint dwFlags, dwLoops; public nint lpNext, reserved; }

    [DllImport("winmm.dll")] private static extern int waveInOpen(out nint h, int device, ref WAVEFORMATEX fmt, nint callback, nint instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(nint h);
    [DllImport("winmm.dll")] private static extern int waveInReset(nint h);
    [DllImport("winmm.dll")] private static extern int waveInClose(nint h);
    [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();

    public int SampleRate { get; } = 16000;
    public event Action<byte[]>? DataAvailable;
    public event Action<string>? Error;

    private nint _handle;
    private readonly List<nint> _headers = [];
    private AutoResetEvent? _signal;
    private Thread? _thread;
    private volatile bool _running;

    public static bool HasMicrophone() => waveInGetNumDevs() > 0;

    public void Start()
    {
        if (_running) return;
        if (!HasMicrophone()) throw new InvalidOperationException("No microphone was found.");
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)SampleRate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = (uint)SampleRate * 2 };
        _signal = new AutoResetEvent(false);
        var rc = waveInOpen(out _handle, WAVE_MAPPER, ref fmt, _signal.SafeWaitHandle.DangerousGetHandle(), 0, CALLBACK_EVENT);
        if (rc != 0) throw new InvalidOperationException($"Could not open the microphone (code {rc}). Check Windows microphone privacy settings.");
        var size = Marshal.SizeOf<WAVEHDR>();
        var bytes = SampleRate * 2 * BufferMs / 1000;
        for (var i = 0; i < BufferCount; i++)
        {
            var hdr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(new WAVEHDR { lpData = Marshal.AllocHGlobal(bytes), dwBufferLength = (uint)bytes }, hdr, false);
            waveInPrepareHeader(_handle, hdr, size);
            waveInAddBuffer(_handle, hdr, size);
            _headers.Add(hdr);
        }
        _running = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "VoxStride mic" };
        _thread.Start();
        waveInStart(_handle);
    }

    private void Pump()
    {
        var size = Marshal.SizeOf<WAVEHDR>();
        while (_running)
        {
            _signal!.WaitOne(200);
            foreach (var hdr in _headers)
            {
                if (!_running) break;
                var h = Marshal.PtrToStructure<WAVEHDR>(hdr);
                if ((h.dwFlags & WHDR_DONE) == 0) continue;
                if (h.dwBytesRecorded > 0)
                {
                    var data = new byte[h.dwBytesRecorded];
                    Marshal.Copy(h.lpData, data, 0, data.Length);
                    try { DataAvailable?.Invoke(data); } catch (Exception ex) { Error?.Invoke(ex.Message); }
                }
                h.dwFlags &= ~(uint)WHDR_DONE;
                h.dwBytesRecorded = 0;
                Marshal.StructureToPtr(h, hdr, false);
                if (_running) waveInAddBuffer(_handle, hdr, size);
            }
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _signal?.Set();
        _thread?.Join(1000);
        waveInReset(_handle);
        var size = Marshal.SizeOf<WAVEHDR>();
        foreach (var hdr in _headers)
        {
            waveInUnprepareHeader(_handle, hdr, size);
            Marshal.FreeHGlobal(Marshal.PtrToStructure<WAVEHDR>(hdr).lpData);
            Marshal.FreeHGlobal(hdr);
        }
        _headers.Clear();
        waveInClose(_handle);
        _handle = 0;
        _signal?.Dispose();
        _signal = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Cloud speech recognition for accented and mixed-language speech: records only while VoxStride is listening,
/// splits speech at pauses, and transcribes each utterance with OpenAI or Gemini. Shows "…" while an utterance is
/// being transcribed (a pending segment, so nothing can commit early). Never runs in standby.
/// </summary>
public sealed class CloudSpeechRecognizer : ISpeechRecognizer
{
    private readonly ITranscriber _transcriber;
    private readonly WaveInRecorder _mic = new();
    private readonly Lock _gate = new();
    private Channel<(AudioSegment seg, string id, Guid session)>? _queue;
    private Task? _worker;
    private UtteranceSegmenter? _segmenter;
    private Guid _session;
    private long _sequence;
    private int _counter;
    private string? _openId;
    private string _context = "";

    public CloudSpeechRecognizer(ITranscriber transcriber, double sensitivity = 3.0)
    {
        _transcriber = transcriber;
        Sensitivity = sensitivity;
        _mic.DataAvailable += d => { lock (_gate) _segmenter?.Push(d); };
        _mic.Error += m => Error?.Invoke(m);
    }

    public double Sensitivity { get; }
    public string Name => "Cloud speech (" + _transcriber.Name + ")";
    public event Action<TranscriptEvent>? Transcript;
    public event Action<string>? Error;

    public Task StartAsync(Guid sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            _session = sessionId;
            if (_segmenter is not null) return Task.CompletedTask; // already listening; session switched
            _context = "";
            _segmenter = new UtteranceSegmenter(_mic.SampleRate) { Sensitivity = Sensitivity };
            _segmenter.SpeechStarted += startMs =>
            {
                var id = "c-" + Interlocked.Increment(ref _counter);
                _openId = id;
                Raise(new TranscriptEvent(_session, id, Interlocked.Increment(ref _sequence), 1, false, "…", startMs, startMs));
            };
            _segmenter.SpeechDiscarded += () =>
            {
                if (_openId is { } id) Raise(new TranscriptEvent(_session, id, Interlocked.Increment(ref _sequence), 2, true, "", 0, 0));
                _openId = null;
            };
            _segmenter.SegmentReady += seg =>
            {
                var id = _openId ?? "c-" + Interlocked.Increment(ref _counter);
                _openId = null;
                _queue?.Writer.TryWrite((seg, id, _session));
            };
            _queue = Channel.CreateUnbounded<(AudioSegment, string, Guid)>(new UnboundedChannelOptions { SingleReader = true });
            _worker = Task.Run(() => WorkAsync(_queue.Reader));
        }
        try { _mic.Start(); }
        catch (Exception)
        {
            lock (_gate) { _segmenter = null; _queue?.Writer.TryComplete(); }
            throw;
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
            if (!r.Ok) Error?.Invoke("I couldn't transcribe that part (" + r.Error + ")");
            var text = r.Text;
            if (text.Length > 0) _context = (_context + " " + text).Trim();
            Raise(new TranscriptEvent(session, id, Interlocked.Increment(ref _sequence), 2, true, text, seg.StartMs, seg.EndMs, r.Ok ? 0.95 : 0));
        }
    }

    private void Raise(TranscriptEvent e) => Transcript?.Invoke(e);

    public async Task StopAsync(TimeSpan flushTimeout, CancellationToken ct)
    {
        Channel<(AudioSegment, string, Guid)>? q;
        Task? worker;
        _mic.Stop();
        lock (_gate)
        {
            if (flushTimeout > TimeSpan.Zero) _segmenter?.Flush();
            _segmenter = null;
            q = _queue; worker = _worker;
            _queue = null; _worker = null;
        }
        q?.Writer.TryComplete();
        if (worker is not null && flushTimeout > TimeSpan.Zero)
            await Task.WhenAny(worker, Task.Delay(flushTimeout, ct)).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _mic.Dispose();
        return ValueTask.CompletedTask;
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

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        if (!local.Enabled || string.IsNullOrWhiteSpace(text)) return;
        StopSpeaking();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _current = cts;
        byte[]? wav = null;
        if (Cloud is { } c)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try { wav = await c.SynthesizeAsync(text, timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (cts.IsCancellationRequested) return;
        if (wav is null || wav.Length < 100) { await local.SpeakAsync(text, cts.Token).ConfigureAwait(false); return; }
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
