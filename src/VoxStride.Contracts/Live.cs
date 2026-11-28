using System.Text.Json.Nodes;

namespace VoxStride.Contracts;

/// <summary>What a real-time voice session is configured with (provider-neutral; Gemini Live implements it).</summary>
public sealed record LiveSetup(
    string SystemInstruction,
    IReadOnlyList<ToolDefinition> Tools,
    string Voice,
    bool WebSearch,
    string? ResumeHandle);

public sealed record LiveFunctionCall(string Id, string Name, JsonObject Args);

public sealed record LiveToolResponse(string Id, string Name, JsonObject Response);

/// <summary>Messages from the live voice service, already decoded.</summary>
public abstract record LiveServerEvent;
public sealed record LiveSetupComplete(string Model) : LiveServerEvent;
/// <summary>A piece of the assistant's spoken reply: 16-bit mono PCM.</summary>
public sealed record LiveAudio(byte[] Pcm, int SampleRate) : LiveServerEvent;
/// <summary>Transcription of what the user said (arrives in pieces).</summary>
public sealed record LiveInputText(string Text) : LiveServerEvent;
/// <summary>Transcription of what the assistant is saying (arrives in pieces).</summary>
public sealed record LiveOutputText(string Text) : LiveServerEvent;
public sealed record LiveTurnComplete : LiveServerEvent;
/// <summary>The user started talking over the assistant: stop playback now.</summary>
public sealed record LiveInterrupted : LiveServerEvent;
public sealed record LiveToolCall(IReadOnlyList<LiveFunctionCall> Calls) : LiveServerEvent;
public sealed record LiveToolCancel(IReadOnlyList<string> Ids) : LiveServerEvent;
public sealed record LiveGoAway(TimeSpan? TimeLeft) : LiveServerEvent;
public sealed record LiveResumeHandle(string Handle) : LiveServerEvent;
public sealed record LiveClosed(string Reason, bool IsError) : LiveServerEvent;
/// <summary>Tokens the service reports for a response (audio, text and images all count).</summary>
public sealed record LiveUsage(long InputTokens, long OutputTokens) : LiveServerEvent;

/// <summary>A bidirectional real-time voice connection (one session).</summary>
public interface ILiveTransport : IAsyncDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    /// <summary>Raised for every decoded server message, in order, on the transport's receive thread.</summary>
    event Action<LiveServerEvent>? Received;
    Task ConnectAsync(LiveSetup setup, CancellationToken ct);
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken ct);
    /// <summary>Tells the service the microphone stream paused (local silence), so it can finish the turn.</summary>
    Task SendAudioStreamEndAsync(CancellationToken ct);
    Task SendImageAsync(byte[] image, string mimeType, CancellationToken ct);
    Task SendTextAsync(string text, CancellationToken ct);
    Task SendToolResponseAsync(IReadOnlyList<LiveToolResponse> responses, CancellationToken ct);
}

/// <summary>Streams the assistant's voice to the speakers and stops instantly when interrupted.</summary>
public interface ILiveAudioOut : IDisposable
{
    void Enqueue(byte[] pcm, int sampleRate);
    /// <summary>Stops playback immediately and drops anything queued.</summary>
    void Clear();
    bool IsPlaying { get; }
    /// <summary>Loudness (0..1) of the audio playing right now, for lip sync.</summary>
    double Level { get; }
}

public enum CompanionState { Off, Connecting, Listening, Hearing, Thinking, Speaking, Working, AwaitingYes, Reconnecting, Error }

public enum CaptionSpeaker { User, Assistant, System }

public sealed record CompanionCaption(CaptionSpeaker Speaker, string Text, bool TurnEnded);
