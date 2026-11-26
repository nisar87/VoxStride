using System.Text.Json.Nodes;

namespace VoxStride.Contracts;

public enum ProviderKind { Claude, OpenAI, Gemini }

public enum ProviderErrorKind { Unauthorized, RateLimited, Timeout, Disconnected, InvalidRequest, Refused, Server, UnsupportedModel, BudgetExceeded, Unknown }

public sealed record ProviderCapabilities(
    bool ToolCalling,
    bool Streaming,
    bool Vision,
    int MaxToolsPerRequest);

public sealed record ProviderSettings(
    ProviderKind Kind,
    string Model,
    string ApiKey,
    Uri? BaseUri = null,
    TimeSpan? RequestTimeout = null,
    int MaxOutputTokens = 2048);

/// <summary>Provider-neutral streamed event (spec §15.1).</summary>
public abstract record ProviderEvent;
public sealed record TextDeltaEvent(string Text) : ProviderEvent;
public sealed record ToolCallStartedEvent(string CallId, string Name) : ProviderEvent;
public sealed record ToolArgsDeltaEvent(string CallId, string Delta) : ProviderEvent;
/// <summary>Complete candidate arguments. The broker still parses, validates schema, binds scope and approval.</summary>
public sealed record ToolCallCompletedEvent(string CallId, string Name, string RawArguments) : ProviderEvent;
public sealed record UsageEvent(long InputTokens, long OutputTokens) : ProviderEvent;
public sealed record CompletedEvent(string StopReason) : ProviderEvent;
public sealed record ProviderErrorEvent(ProviderErrorKind Kind, string Message, int? HttpStatus = null) : ProviderEvent;

public sealed record ToolCallOutput(string CallId, string Name, JsonObject Result, bool IsError);

/// <summary>
/// A single provider conversation. Adapters keep the provider-native history internally so vendor continuation
/// data (Claude content blocks, OpenAI output items, Gemini thought signatures) is replayed exactly.
/// Raw histories are never combined across providers.
/// </summary>
public interface IProviderConversation
{
    void AddUserText(string text);
    void AddToolResults(IReadOnlyList<ToolCallOutput> outputs);
    IAsyncEnumerable<ProviderEvent> StreamAsync(CancellationToken ct);
}

public interface IReasoningProvider
{
    ProviderKind Kind { get; }
    string Model { get; }
    ProviderCapabilities Capabilities { get; }
    IProviderConversation StartConversation(string systemInstruction, IReadOnlyList<ToolDefinition> tools);
    /// <summary>Minimal live request to validate the key and model. Sends no user data.</summary>
    Task<ProviderCheckResult> CheckAsync(CancellationToken ct);
}

public sealed record ProviderCheckResult(bool Ok, string Message, ProviderErrorKind? Error = null);
