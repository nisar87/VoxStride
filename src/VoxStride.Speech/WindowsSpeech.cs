using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using VoxStride.Contracts;

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

/// <summary>
/// Local streaming dictation using the installed Windows speech recognizer (no network, no keys).
/// Emits revisioned interim hypotheses and finals per utterance with audio-time ordering. Control phrases
/// are added as a small grammar for accuracy; the coordinator — not this class — decides what they mean.
/// </summary>
public sealed class WindowsSpeechRecognizer : ISpeechRecognizer
{
    private readonly string _culture;
    private readonly IReadOnlyList<string> _controlPhrases;
    private SpeechRecognitionEngine? _engine;
    private Guid _session;
    private int _segment;
    private int _revision;
    private long _sequence;
    private long _segmentStartMs;
    private TaskCompletionSource? _stopped;
    private readonly Lock _gate = new();

    public WindowsSpeechRecognizer(string culture, IReadOnlyList<string> controlPhrases)
    {
        _culture = culture;
        _controlPhrases = controlPhrases;
    }

    public string Name => "Windows speech recognition (local)";
    public event Action<TranscriptEvent>? Transcript;
    public event Action<string>? Error;

    public Task StartAsync(Guid sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            _session = sessionId;
            if (_engine is null)
            {
                var info = SpeechEnvironment.PickRecognizer(_culture) ?? throw new InvalidOperationException("No Windows speech recognizer is installed. Add an English speech pack in Settings > Time & language > Speech, or type your instruction.");
                _engine = new SpeechRecognitionEngine(info);
                var dictation = new DictationGrammar { Name = "dictation", Weight = 0.9f };
                _engine.LoadGrammar(dictation);
                if (_controlPhrases.Count > 0)
                {
                    var g = new Grammar(new GrammarBuilder(new Choices(_controlPhrases.ToArray())) { Culture = info.Culture }) { Name = "control", Weight = 1.0f, Priority = 1 };
                    _engine.LoadGrammar(g);
                }
                _engine.SpeechDetected += (_, e) => { lock (_gate) { _segment++; _revision = 0; _segmentStartMs = (long)e.AudioPosition.TotalMilliseconds; } };
                _engine.SpeechHypothesized += (_, e) => Emit(e.Result.Text, isFinal: false, e.Result.Confidence, e.Result.Audio?.AudioPosition);
                _engine.SpeechRecognized += (_, e) => Emit(e.Result.Text, isFinal: true, e.Result.Confidence, e.Result.Audio?.AudioPosition);
                _engine.SpeechRecognitionRejected += (_, e) =>
                {
                    // Rejected audio: finalize the segment with its best guess at low confidence so pending interims don't block;
                    // control detection treats low confidence as uncertain and never commits on it.
                    var alt = e.Result.Alternates.FirstOrDefault();
                    Emit(alt?.Text ?? "", isFinal: true, alt?.Confidence ?? 0, e.Result.Audio?.AudioPosition);
                };
                _engine.AudioSignalProblemOccurred += (_, e) =>
                {
                    if (e.AudioSignalProblem is AudioSignalProblem.NoSignal) Error?.Invoke("No microphone signal");
                };
                _engine.RecognizeCompleted += (_, e) =>
                {
                    if (e.Error is not null) Error?.Invoke(e.Error.Message);
                    _stopped?.TrySetResult();
                };
            }
            try { _engine.SetInputToDefaultAudioDevice(); }
            catch (Exception ex) when (ex is InvalidOperationException or COMException) { throw new InvalidOperationException("No microphone is available (" + ex.Message + ")."); }
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _engine.RecognizeAsync(RecognizeMode.Multiple);
        }
        return Task.CompletedTask;
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
        Task? wait;
        lock (_gate)
        {
            if (_engine is null || _stopped is null) return;
            wait = _stopped.Task;
            if (flushTimeout <= TimeSpan.Zero) _engine.RecognizeAsyncCancel();
            else _engine.RecognizeAsyncStop(); // completes the current phrase, then stops
        }
        await Task.WhenAny(wait, Task.Delay(flushTimeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(300) : flushTimeout, ct)).ConfigureAwait(false);
        lock (_gate)
        {
            if (!wait.IsCompleted) _engine?.RecognizeAsyncCancel();
            try { _engine?.SetInputToNull(); } catch (Exception) { } // release the microphone
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { _session = Guid.Empty; _engine?.Dispose(); _engine = null; }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Local wake phrase detection with a constrained Windows speech grammar ("Hey VoxStride" + variants).
/// Optionally captures words spoken immediately after the wake phrase so the start of a command isn't clipped.
/// Confidence-gated; false-trigger rate must be measured on real audio (docs/benchmarks).
/// </summary>
public sealed class WindowsWakeWordDetector : IWakeWordDetector
{
    private readonly string _culture;
    private SpeechRecognitionEngine? _engine;
    private readonly Lock _gate = new();
    public float MinConfidence { get; set; } = 0.72f;
    public string Phrase => "Hey VoxStride";
    public event Action<DateTimeOffset>? WakeDetected;
    /// <summary>Raised with any words captured after the wake phrase in the same utterance.</summary>
    public event Action<string>? TrailingText;

    public static readonly string[] Variants = ["hey voxstride", "hey vox stride", "okay voxstride", "ok vox stride", "hi voxstride"];

    public WindowsWakeWordDetector(string culture) { _culture = culture; }

    public Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_engine is null)
            {
                var info = SpeechEnvironment.PickRecognizer(_culture) ?? throw new InvalidOperationException("No Windows speech recognizer is installed.");
                _engine = new SpeechRecognitionEngine(info);
                var wake = new GrammarBuilder(new Choices(Variants)) { Culture = info.Culture };
                _engine.LoadGrammar(new Grammar(wake) { Name = "wake" });
                var wakeWithText = new GrammarBuilder(new Choices(Variants)) { Culture = info.Culture };
                wakeWithText.AppendDictation();
                _engine.LoadGrammar(new Grammar(wakeWithText) { Name = "wake+dictation", Weight = 0.8f });
                _engine.SpeechRecognized += (_, e) =>
                {
                    if (e.Result.Confidence < MinConfidence) return;
                    var words = e.Result.Words;
                    var wakeIdx = words.Count > 1 && words[1].Text.Equals("vox", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
                    if (words.Take(wakeIdx).Any(w => w.Confidence < MinConfidence * 0.8f)) return;
                    WakeDetected?.Invoke(DateTimeOffset.UtcNow);
                    if (words.Count > wakeIdx) TrailingText?.Invoke(string.Join(' ', words.Skip(wakeIdx).Select(w => w.Text)));
                };
            }
            _engine.SetInputToDefaultAudioDevice();
            _engine.RecognizeAsync(RecognizeMode.Multiple);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            try { _engine?.RecognizeAsyncCancel(); _engine?.SetInputToNull(); } catch (Exception) { }
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { _engine?.Dispose(); _engine = null; }
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
