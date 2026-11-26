using System.Diagnostics;
using VoxStride.Contracts;
using Windows.Media.SpeechRecognition;

namespace VoxStride.Speech;

/// <summary>
/// Modern Windows dictation (Windows.Media.SpeechRecognition, the engine behind Win+H). Far more accurate than the
/// legacy SAPI engine and supports accent-specific languages such as English (India). Uses Microsoft's online speech
/// service, governed by Windows Settings › Privacy &amp; security › Speech › "Online speech recognition".
/// Runs only while VoxStride is listening; no API key needed.
/// </summary>
public sealed class WinRtSpeechRecognizer : ISpeechRecognizer
{
    private readonly string _culture;
    private SpeechRecognizer? _rec;
    private Guid _session;
    private int _segment = 1;
    private int _revision;
    private long _sequence;
    private readonly Stopwatch _clock = new();
    private long _segmentStart;
    private TaskCompletionSource? _stopped;
    private bool _running;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WinRtSpeechRecognizer(string culture) { _culture = culture; }

    public string Name => "Windows online dictation (" + (_rec?.CurrentLanguage.DisplayName ?? _culture) + ")";
    public event Action<TranscriptEvent>? Transcript;
    public event Action<string>? Error;

    /// <summary>Dictation languages this PC supports (BCP-47 tags).</summary>
    public static IReadOnlyList<(string Tag, string Name)> Languages()
    {
        try { return SpeechRecognizer.SupportedTopicLanguages.Select(l => (l.LanguageTag, l.DisplayName)).ToList(); }
        catch (Exception) { return []; }
    }

    public static bool IsAvailable() => Languages().Count > 0;

    public async Task StartAsync(Guid sessionId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _session = sessionId;
            if (_running) return;
            if (_rec is null)
            {
                var langs = SpeechRecognizer.SupportedTopicLanguages;
                var lang = langs.FirstOrDefault(l => l.LanguageTag.Equals(_culture, StringComparison.OrdinalIgnoreCase))
                           ?? langs.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                           ?? SpeechRecognizer.SystemSpeechLanguage;
                var rec = new SpeechRecognizer(lang);
                rec.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
                SpeechRecognitionCompilationResult compiled;
                try { compiled = await rec.CompileConstraintsAsync(); }
                catch (Exception ex) when ((uint)ex.HResult == 0x80045509)
                {
                    rec.Dispose();
                    throw new InvalidOperationException("Windows online speech recognition is turned off. Turn it on in Windows Settings › Privacy & security › Speech, then try again.");
                }
                if (compiled.Status != SpeechRecognitionResultStatus.Success)
                {
                    rec.Dispose();
                    throw new InvalidOperationException("Windows dictation isn't available (" + compiled.Status + ").");
                }
                rec.Timeouts.EndSilenceTimeout = TimeSpan.FromMilliseconds(900);
                rec.Timeouts.BabbleTimeout = TimeSpan.Zero;
                rec.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromMinutes(10); // VoxStride decides when to stop, never silence
                rec.HypothesisGenerated += (_, e) =>
                {
                    if (_revision == 0) _segmentStart = _clock.ElapsedMilliseconds;
                    Emit(e.Hypothesis.Text, false, null);
                };
                rec.ContinuousRecognitionSession.ResultGenerated += (_, e) =>
                {
                    var r = e.Result;
                    if (_revision == 0) _segmentStart = _clock.ElapsedMilliseconds;
                    var conf = r.Confidence switch
                    {
                        SpeechRecognitionConfidence.High => 0.95,
                        SpeechRecognitionConfidence.Medium => 0.8,
                        SpeechRecognitionConfidence.Low => 0.5,
                        _ => 0.2,
                    };
                    Emit(r.Status == SpeechRecognitionResultStatus.Success ? r.Text : "", true, conf);
                    _segment++;
                    _revision = 0;
                };
                rec.ContinuousRecognitionSession.Completed += (_, e) =>
                {
                    _running = false;
                    if (_revision > 0) { Emit("", true, 0); _segment++; _revision = 0; } // close any open hypothesis
                    if (e.Status is not (SpeechRecognitionResultStatus.Success or SpeechRecognitionResultStatus.UserCanceled))
                        Error?.Invoke(e.Status switch
                        {
                            SpeechRecognitionResultStatus.NetworkFailure => "Windows dictation needs an internet connection",
                            SpeechRecognitionResultStatus.MicrophoneUnavailable => "the microphone isn't available — check Windows Settings › Privacy › Microphone",
                            SpeechRecognitionResultStatus.AudioQualityFailure => "the audio quality is too low — move closer to the microphone",
                            SpeechRecognitionResultStatus.TimeoutExceeded => "listening timed out",
                            _ => "Windows dictation stopped (" + e.Status + ")",
                        });
                    _stopped?.TrySetResult();
                };
                _rec = rec;
            }
            _clock.Restart();
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _rec.ContinuousRecognitionSession.StartAsync();
            _running = true;
        }
        finally { _gate.Release(); }
    }

    private void Emit(string text, bool isFinal, double? confidence)
    {
        if (_session == Guid.Empty) return;
        Transcript?.Invoke(new TranscriptEvent(_session, "w-" + _segment, Interlocked.Increment(ref _sequence), ++_revision, isFinal, text,
            _segmentStart, _clock.ElapsedMilliseconds, confidence));
    }

    public async Task StopAsync(TimeSpan flushTimeout, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_rec is null || !_running) return;
            var wait = _stopped?.Task ?? Task.CompletedTask;
            try
            {
                if (flushTimeout <= TimeSpan.Zero) await _rec.ContinuousRecognitionSession.CancelAsync();
                else await _rec.ContinuousRecognitionSession.StopAsync(); // finalizes the phrase in progress
            }
            catch (Exception) { /* already stopped */ }
            await Task.WhenAny(wait, Task.Delay(flushTimeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(500) : flushTimeout, ct)).ConfigureAwait(false);
            _running = false;
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        _session = Guid.Empty;
        _rec?.Dispose();
        _rec = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Uses the preferred recognizer, and falls back to a second one (once, with a notice) if it can't start.</summary>
public sealed class FallbackSpeechRecognizer(ISpeechRecognizer primary, ISpeechRecognizer fallback) : ISpeechRecognizer
{
    private ISpeechRecognizer _active = primary;
    private bool _fellBack;

    public string Name => _active.Name;
    public event Action<TranscriptEvent>? Transcript;
    public event Action<string>? Error;
    public event Action<string>? FellBack;

    private bool _wired;
    private void Wire()
    {
        if (_wired) return;
        _wired = true;
        primary.Transcript += e => { if (ReferenceEquals(_active, primary)) Transcript?.Invoke(e); };
        fallback.Transcript += e => { if (ReferenceEquals(_active, fallback)) Transcript?.Invoke(e); };
        primary.Error += m => { if (ReferenceEquals(_active, primary)) Error?.Invoke(m); };
        fallback.Error += m => { if (ReferenceEquals(_active, fallback)) Error?.Invoke(m); };
    }

    public async Task StartAsync(Guid sessionId, CancellationToken ct)
    {
        Wire();
        if (!_fellBack)
        {
            try { await primary.StartAsync(sessionId, ct).ConfigureAwait(false); return; }
            catch (Exception ex)
            {
                _fellBack = true;
                _active = fallback;
                FellBack?.Invoke(ex.Message);
            }
        }
        await fallback.StartAsync(sessionId, ct).ConfigureAwait(false);
    }

    public Task StopAsync(TimeSpan flushTimeout, CancellationToken ct) => _active.StopAsync(flushTimeout, ct);

    public async ValueTask DisposeAsync()
    {
        await primary.DisposeAsync().ConfigureAwait(false);
        await fallback.DisposeAsync().ConfigureAwait(false);
    }
}
