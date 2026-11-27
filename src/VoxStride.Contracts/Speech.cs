namespace VoxStride.Contracts;

/// <summary>Streaming speech recognizer. Emits revisioned transcript events; never decides when a command ends.</summary>
public interface ISpeechRecognizer : IAsyncDisposable
{
    string Name { get; }
    event Action<TranscriptEvent>? Transcript;
    event Action<string>? Error;
    Task StartAsync(Guid sessionId, CancellationToken ct);
    /// <summary>Stops capture and flushes pending recognition; returns after the last final segment or the timeout.</summary>
    Task StopAsync(TimeSpan flushTimeout, CancellationToken ct);
}

/// <summary>Optional: a recognizer that reports non-fatal hints about the microphone or audio.</summary>
public interface ISpeechDiagnostics
{
    /// <summary>A gentle, non-fatal hint (e.g. "I haven't heard any sound yet"). Never interrupts the instruction.</summary>
    event Action<string>? Hint;
}

public interface ISpeechSynthesizer
{
    bool IsSpeaking { get; }
    Task SpeakAsync(string text, CancellationToken ct);
    void PlayCue(CueKind cue);
    void StopSpeaking();
}

public enum CueKind { Wake, Commit, Done, Error, Cancelled }

public interface IWakeWordDetector : IAsyncDisposable
{
    string Phrase { get; }
    event Action<DateTimeOffset>? WakeDetected;
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
