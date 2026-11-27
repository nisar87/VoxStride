using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using VoxStride.Contracts;
using VoxStride.Core.Voice;

namespace VoxStride.Speech;

/// <summary>Information about locally installed Windows speech engines.</summary>
public static class SpeechEnvironment
{
    public static IReadOnlyList<string> RecognizerCultures()
    {
        try { return SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture.Name).Distinct().ToList(); }
        catch (Exception) { return []; }
    }

    public static RecognizerInfo? PickRecognizer(string preferredCulture)
    {
        IReadOnlyList<RecognizerInfo> all;
        try { all = SpeechRecognitionEngine.InstalledRecognizers(); } catch (Exception) { return null; }
        return all.FirstOrDefault(r => r.Culture.Name.Equals(preferredCulture, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
            ?? all.FirstOrDefault();
    }

    public static IReadOnlyList<string> Voices()
    {
        try { using var s = new SpeechSynthesizer(); return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList(); }
        catch (Exception) { return []; }
    }
}

/// <summary>Optional diagnostics a recognizer can raise besides hard errors.</summary>
public interface ISpeechDiagnostics
{
    /// <summary>A gentle, non-fatal hint (e.g. "I haven't heard any sound yet"). Never interrupts the instruction.</summary>
    event Action<string>? Hint;
}

/// <summary>
/// Local streaming dictation using the installed Windows speech recognizer (no network, no keys).
/// Audio comes from the shared <see cref="AudioHub"/> through a <see cref="PcmPipeStream"/>, which fills every engine
/// read completely — SAPI ends recognition on a short read. Each listening session owns one microphone lease that is
/// released on every path (stop, cancel, engine ending on its own, dispose).
/// </summary>
public sealed class WindowsSpeechRecognizer : ISpeechRecognizer, ISpeechDiagnostics
{
    private readonly string _culture;
    private readonly IReadOnlyList<string> _controlPhrases;
    private readonly AudioHub _mic;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private SpeechRecognitionEngine? _engine;
    private Run? _active;    // the session currently accepting speech
    private Run? _engineRun; // the run the engine is processing (cleared when the engine completes)
    private Guid _session;
    private int _segment;
    private int _revision;
    private long _sequence;
    private long _segmentStartMs;

    private sealed class Run(AudioLease lease, PcmPipeStream pipe)
    {
        public AudioLease Lease { get; } = lease;
        public PcmPipeStream Pipe { get; } = pipe;
        public MicWatchdog? Watchdog { get; set; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopping { get; set; }
        public int Hypotheses, Finals, Rejected;
        public int CleanedUp;
    }

    public WindowsSpeechRecognizer(string culture, IReadOnlyList<string> controlPhrases, AudioHub microphone, Action<string>? log = null)
    {
        _culture = culture;
        _controlPhrases = controlPhrases;
        _mic = microphone;
        _log = log;
        _mic.DeviceError += m => { if (_active is not null) Error?.Invoke(m); };
    }

    public string Name => "Windows speech recognition (offline)";
    public event Action<TranscriptEvent>? Transcript;
    public event Action<string>? Error;
    public event Action<string>? Hint;

    public async Task StartAsync(Guid sessionId, CancellationToken ct)
    {
        Task? previous;
        lock (_gate)
        {
            _session = sessionId;
            if (_active is not null) return; // already listening: only the session id changes
            previous = _engineRun?.Done.Task;
        }
        // The engine can't start a new recognition until the previous one has fully completed.
        if (previous is { IsCompleted: false })
            await Task.WhenAny(previous, Task.Delay(1500, ct)).ConfigureAwait(false);

        SpeechRecognitionEngine? stale = null;
        lock (_gate)
        {
            if (_active is null && _engineRun is not null)
            {
                _log?.Invoke("speech-engine-reset: previous recognition never completed");
                stale = DetachEngine();
            }
        }
        DisposeEngine(stale);

        lock (_gate)
        {
            if (_active is not null) return;
            var engine = EnsureEngine();
            var lease = _mic.Acquire("dictation"); // throws a clear message if the microphone can't be opened
            var pipe = new PcmPipeStream();
            lease.Data += pipe.Append;
            var run = new Run(lease, pipe);
            try
            {
                engine.SetInputToAudioStream(pipe, new SpeechAudioFormatInfo(lease.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                engine.RecognizeAsync(RecognizeMode.Multiple);
            }
            catch
            {
                Cleanup(run, "start failed");
                throw;
            }
            run.Watchdog = new MicWatchdog(lease, m => Error?.Invoke(m), m => Hint?.Invoke(m));
            _active = run;
            _engineRun = run;
            _log?.Invoke($"speech-start engine=offline culture={engine.RecognizerInfo.Culture.Name} device=\"{lease.DeviceLabel}\"");
        }
    }

    private SpeechRecognitionEngine EnsureEngine()
    {
        if (_engine is not null) return _engine;
        var info = SpeechEnvironment.PickRecognizer(_culture) ?? throw new InvalidOperationException("No Windows speech recognizer is installed. Add an English speech pack in Settings › Time & language › Speech, or type your instruction.");
        var engine = new SpeechRecognitionEngine(info);
        engine.LoadGrammar(new DictationGrammar { Name = "dictation", Weight = 0.9f });
        if (_controlPhrases.Count > 0)
            engine.LoadGrammar(new Grammar(new GrammarBuilder(new Choices(_controlPhrases.ToArray())) { Culture = info.Culture }) { Name = "control", Weight = 1.0f, Priority = 1 });
        // Live audio: give the engine room to wait for the end of a phrase instead of cutting it short.
        engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(700);
        engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(1000);
        engine.BabbleTimeout = TimeSpan.Zero;
        engine.InitialSilenceTimeout = TimeSpan.Zero;
        engine.SpeechDetected += (_, e) => { lock (_gate) { _segment++; _revision = 0; _segmentStartMs = (long)e.AudioPosition.TotalMilliseconds; } };
        engine.SpeechHypothesized += (_, e) => { if (_engineRun is { } r) Interlocked.Increment(ref r.Hypotheses); Emit(e.Result.Text, false, e.Result.Confidence, e.Result.Audio); };
        engine.SpeechRecognized += (_, e) =>
        {
            if (_engineRun is { } r) Interlocked.Increment(ref r.Finals);
            Emit(e.Result.Text, true, e.Result.Confidence, e.Result.Audio);
        };
        engine.SpeechRecognitionRejected += (_, e) =>
        {
            // Finalize the segment with its best guess at low confidence so pending interims don't block;
            // control detection treats low confidence as uncertain and never commits on it.
            if (_engineRun is { } r) Interlocked.Increment(ref r.Rejected);
            var alt = e.Result.Alternates.FirstOrDefault();
            Emit(alt?.Text ?? "", true, alt?.Confidence ?? 0, e.Result.Audio);
        };
        engine.RecognizeCompleted += (sender, e) =>
        {
            Run? run;
            bool unexpected;
            lock (_gate)
            {
                if (!ReferenceEquals(sender, _engine)) return; // a replaced engine finishing late
                run = _engineRun;
                _engineRun = null;
                unexpected = run is not null && !run.Stopping;
                if (unexpected && ReferenceEquals(_active, run)) _active = null;
            }
            if (run is null) return;
            if (unexpected)
            {
                _log?.Invoke("speech-engine-ended-unexpectedly: " + (e.Error?.Message ?? "end of audio"));
                Error?.Invoke(e.Error is not null ? "speech recognition stopped: " + e.Error.Message : "speech recognition stopped unexpectedly. Press Talk to try again, or type your instruction");
            }
            Cleanup(run, unexpected ? "engine ended" : "stopped");
            run.Done.TrySetResult();
        };
        _engine = engine;
        return engine;
    }

    /// <summary>Detaches the engine (call under the lock); dispose the returned engine outside the lock.</summary>
    private SpeechRecognitionEngine? DetachEngine()
    {
        var old = _engine;
        _engine = null; // late events from it are ignored (sender check)
        if (_engineRun is { } r) { Cleanup(r, "engine reset"); r.Done.TrySetResult(); }
        _engineRun = null;
        return old;
    }

    /// <summary>Engine calls never run under our lock: its event threads take the same lock.</summary>
    private static void DisposeEngine(SpeechRecognitionEngine? engine)
    {
        if (engine is null) return;
        try { engine.RecognizeAsyncCancel(); } catch (Exception) { }
        try { engine.Dispose(); } catch (Exception) { }
    }

    private void Cleanup(Run run, string reason)
    {
        if (Interlocked.Exchange(ref run.CleanedUp, 1) != 0) return;
        run.Watchdog?.Dispose();
        run.Pipe.Complete(); // idempotent; unblocks any pending engine read
        _log?.Invoke($"speech-session engine=offline reason={reason} device=\"{run.Lease.DeviceLabel}\" ms={(long)run.Lease.Elapsed.TotalMilliseconds} buffers={run.Lease.Buffers} peak={run.Lease.Peak:0} dropped={run.Pipe.DroppedBytes} hypotheses={run.Hypotheses} finals={run.Finals} rejected={run.Rejected}");
        run.Lease.Dispose();
    }

    private void Emit(string text, bool isFinal, float confidence, RecognizedAudio? audio)
    {
        TranscriptEvent ev;
        lock (_gate)
        {
            if (_session == Guid.Empty) return;
            var start = audio is not null ? (long)audio.AudioPosition.TotalMilliseconds : _segmentStartMs;
            var end = audio is not null ? start + (long)audio.Duration.TotalMilliseconds : start;
            ev = new TranscriptEvent(_session, "seg-" + _segment, ++_sequence, ++_revision, isFinal, text, start, end, confidence);
        }
        Transcript?.Invoke(ev);
    }

    public async Task StopAsync(TimeSpan flushTimeout, CancellationToken ct)
    {
        Run? run;
        SpeechRecognitionEngine? engine;
        lock (_gate)
        {
            run = _active;
            if (run is null) return;
            _active = null;
            run.Stopping = true;
            engine = _engine;
        }
        run.Watchdog?.Dispose();
        run.Lease.Data -= run.Pipe.Append; // no new audio after the user finished
        // Always end the stream BEFORE any engine call: the engine may wait for its current read to return, and
        // that read would otherwise block forever waiting for audio that will never come.
        if (flushTimeout <= TimeSpan.Zero)
        {
            run.Pipe.Abort(); // cancel: discard buffered audio so nothing more is recognized
            try { engine?.RecognizeAsyncCancel(); } catch (Exception) { }
        }
        else run.Pipe.Complete(); // finish: the engine finalizes the last phrase from what it has, then completes
        var wait = flushTimeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(500) : flushTimeout;
        try { await Task.WhenAny(run.Done.Task, Task.Delay(wait, ct)).ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (!run.Done.Task.IsCompleted)
        {
            try { engine?.RecognizeAsyncCancel(); } catch (Exception) { }
            try { await Task.WhenAny(run.Done.Task, Task.Delay(500, ct)).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        Cleanup(run, run.Done.Task.IsCompleted ? "stopped" : "stop timed out"); // the microphone is released even if the engine hangs
    }

    public ValueTask DisposeAsync()
    {
        SpeechRecognitionEngine? engine;
        lock (_gate)
        {
            _session = Guid.Empty;
            if (_active is { } a) Cleanup(a, "dispose");
            _active = null;
            engine = DetachEngine();
        }
        DisposeEngine(engine);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Local "Hey VoxStride" detection with a constrained Windows speech grammar. Listens through a lease on the shared
/// <see cref="AudioHub"/>; if the engine ever stops on its own it is restarted (rate-limited) and the event logged.
/// Optionally captures words spoken right after the wake phrase so the start of a command isn't clipped.
/// </summary>
public sealed class WindowsWakeWordDetector : IWakeWordDetector
{
    private readonly string _culture;
    private readonly AudioHub _mic;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private SpeechRecognitionEngine? _engine;
    private AudioLease? _lease;
    private PcmPipeStream? _pipe;
    private bool _wanted;
    private bool _recognizing;
    private readonly Queue<DateTimeOffset> _restarts = new();

    public float MinConfidence { get; set; } = 0.72f;
    public string Phrase => "Hey VoxStride";
    public event Action<DateTimeOffset>? WakeDetected;
    /// <summary>Raised with any words captured after the wake phrase in the same utterance.</summary>
    public event Action<string>? TrailingText;

    public static readonly string[] Variants = ["hey voxstride", "hey vox stride", "okay voxstride", "ok vox stride", "hi voxstride"];

    public WindowsWakeWordDetector(string culture, AudioHub microphone, Action<string>? log = null)
    {
        _culture = culture;
        _mic = microphone;
        _log = log;
    }

    public bool IsListening { get { lock (_gate) return _lease is not null; } }

    public Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _wanted = true;
            if (_lease is not null) return Task.CompletedTask;
            BeginCore();
        }
        return Task.CompletedTask;
    }

    private void BeginCore()
    {
        if (_recognizing && _engine is { } busy)
        {
            // A cancelled recognition finishes asynchronously; rather than race it, start on a fresh engine.
            _engine = null;
            _recognizing = false;
            _ = Task.Run(() => { try { busy.RecognizeAsyncCancel(); busy.Dispose(); } catch (Exception) { } }); // never under our lock
        }
        var engine = EnsureEngine();
        var lease = _mic.Acquire("wake");
        var pipe = new PcmPipeStream();
        lease.Data += pipe.Append;
        try
        {
            engine.SetInputToAudioStream(pipe, new SpeechAudioFormatInfo(lease.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            engine.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch
        {
            pipe.Complete();
            lease.Dispose();
            throw;
        }
        _recognizing = true;
        _lease = lease;
        _pipe = pipe;
    }

    private SpeechRecognitionEngine EnsureEngine()
    {
        if (_engine is not null) return _engine;
        var info = SpeechEnvironment.PickRecognizer(_culture) ?? throw new InvalidOperationException("No Windows speech recognizer is installed.");
        var engine = new SpeechRecognitionEngine(info);
        engine.LoadGrammar(new Grammar(new GrammarBuilder(new Choices(Variants)) { Culture = info.Culture }) { Name = "wake" });
        var wakeWithText = new GrammarBuilder(new Choices(Variants)) { Culture = info.Culture };
        wakeWithText.AppendDictation();
        engine.LoadGrammar(new Grammar(wakeWithText) { Name = "wake+dictation", Weight = 0.8f });
        engine.SpeechRecognized += (_, e) =>
        {
            if (e.Result.Confidence < MinConfidence) return;
            var words = e.Result.Words;
            var wakeIdx = words.Count > 1 && words[1].Text.Equals("vox", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
            if (words.Take(wakeIdx).Any(w => w.Confidence < MinConfidence * 0.8f)) return;
            _log?.Invoke($"wake-detected confidence={e.Result.Confidence:0.00}");
            WakeDetected?.Invoke(DateTimeOffset.UtcNow);
            if (words.Count > wakeIdx) TrailingText?.Invoke(string.Join(' ', words.Skip(wakeIdx).Select(w => w.Text)));
        };
        engine.RecognizeCompleted += (sender, e) =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(sender, _engine)) return; // a replaced engine finishing late
                _recognizing = false;
                if (!_wanted || _lease is null) return;
                // Should not happen with a live pipe. Release, log, and restart a limited number of times.
                _log?.Invoke("wake-engine-ended-unexpectedly: " + (e.Error?.Message ?? "end of audio"));
                ReleaseCore();
                var now = DateTimeOffset.UtcNow;
                while (_restarts.Count > 0 && now - _restarts.Peek() > TimeSpan.FromMinutes(1)) _restarts.Dequeue();
                if (_restarts.Count >= 3) { _log?.Invoke("wake-engine-gave-up"); _wanted = false; return; }
                _restarts.Enqueue(now);
            }
            _ = Task.Run(async () =>
            {
                await Task.Delay(500).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!_wanted || _lease is not null) return;
                    try { BeginCore(); } catch (Exception ex) { _log?.Invoke("wake-restart-failed: " + ex.Message); }
                }
            });
        };
        _engine = engine;
        return engine;
    }

    private void ReleaseCore()
    {
        _pipe?.Complete();
        _pipe = null;
        _lease?.Dispose();
        _lease = null;
    }

    public Task StopAsync()
    {
        SpeechRecognitionEngine? engine;
        lock (_gate)
        {
            _wanted = false;
            if (_lease is null) return Task.CompletedTask;
            ReleaseCore(); // the microphone lease is released immediately, whatever the engine does
            engine = _engine;
        }
        try { engine?.RecognizeAsyncCancel(); } catch (Exception) { } // engine calls never run under our lock
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SpeechRecognitionEngine? engine;
        lock (_gate)
        {
            _wanted = false;
            ReleaseCore();
            engine = _engine;
            _engine = null;
        }
        if (engine is not null)
        {
            try { engine.RecognizeAsyncCancel(); } catch (Exception) { }
            try { engine.Dispose(); } catch (Exception) { }
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Local TTS with installed Windows voices plus cached in-memory earcons (no network).</summary>
public sealed class WindowsSpeechSynthesizer : ISpeechSynthesizer, IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly Dictionary<CueKind, nint> _cues = new(); // unmanaged copies: async PlaySound reads after the call returns
    public bool Enabled { get; set; } = true;
    public bool CuesEnabled { get; set; } = true;
    public bool IsSpeaking => _synth.State == SynthesizerState.Speaking;

    public WindowsSpeechSynthesizer(string? voice = null, int rate = 1)
    {
        _synth.SetOutputToDefaultAudioDevice();
        _synth.Rate = Math.Clamp(rate, -10, 10);
        if (!string.IsNullOrEmpty(voice)) { try { _synth.SelectVoice(voice); } catch (ArgumentException) { } }
        AddCue(CueKind.Wake, Tone([(880, 70), (1320, 90)]));
        AddCue(CueKind.Commit, Tone([(1046, 80)]));
        AddCue(CueKind.Done, Tone([(784, 70), (1046, 110)]));
        AddCue(CueKind.Error, Tone([(440, 120), (330, 160)]));
        AddCue(CueKind.Cancelled, Tone([(660, 80), (440, 100)]));
    }

    private void AddCue(CueKind kind, byte[] wav)
    {
        var p = Marshal.AllocHGlobal(wav.Length);
        Marshal.Copy(wav, 0, p, wav.Length);
        _cues[kind] = p;
    }

    public Task SpeakAsync(string text, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new Prompt(text);
        void Done(object? s, SpeakCompletedEventArgs e) { if (e.Prompt == prompt) { _synth.SpeakCompleted -= Done; tcs.TrySetResult(); } }
        _synth.SpeakCompleted += Done;
        _synth.SpeakAsync(prompt);
        ct.Register(() => { try { _synth.SpeakAsyncCancel(prompt); } catch (Exception) { } tcs.TrySetResult(); });
        return tcs.Task;
    }

    public void StopSpeaking()
    {
        try { _synth.SpeakAsyncCancelAll(); } catch (Exception) { }
    }

    public void PlayCue(CueKind cue)
    {
        if (!CuesEnabled || !_cues.TryGetValue(cue, out var wav)) return;
        PlaySound(wav, 0, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
    }

    /// <summary>Generates a short 16-bit mono PCM WAV with soft envelopes.</summary>
    internal static byte[] Tone((int hz, int ms)[] notes, int sampleRate = 22050)
    {
        var samples = new List<short>();
        foreach (var (hz, ms) in notes)
        {
            var n = sampleRate * ms / 1000;
            for (var i = 0; i < n; i++)
            {
                var env = Math.Min(1.0, Math.Min(i, n - i) / (sampleRate * 0.008));
                samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / sampleRate) * 9000 * env));
            }
        }
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        var dataLen = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms2.ToArray();
    }

    public void Dispose()
    {
        PlaySound(0, 0, SND_PURGE);
        _synth.Dispose();
        foreach (var p in _cues.Values) Marshal.FreeHGlobal(p);
        _cues.Clear();
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySound(nint sound, nint hmod, uint flags);
    private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4, SND_PURGE = 0x40;
}
