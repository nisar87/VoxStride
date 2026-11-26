using System.Text.Json.Nodes;

namespace VoxStride.Contracts;

/// <summary>Local risk classification. Model-supplied risk labels are advisory only; this is authoritative.</summary>
public enum RiskTier { Read = 0, LowReversible = 1, Moderate = 2, High = 3, Prohibited = 4 }

public enum ToolOutcome { Success, Partial, Failure, Unknown }

public enum InputMode { Voice, Typed, PushToTalk }

/// <summary>User-visible lifecycle states (spec §14.2).</summary>
public enum AgentState
{
    Muted, Standby, Listening, Finalizing, Clarifying, Committed, Planning,
    AwaitingApproval, Executing, Verifying, Speaking, FollowUp, Paused, Cancelled, Failed,
}

/// <summary>Immutable, committed user instruction. Only this can lead to an executable run.</summary>
public sealed record CommandEnvelope(
    Guid SessionId,
    Guid CommandId,
    string FinalizedTranscript,
    string TranscriptHash,
    string Language,
    IReadOnlyList<string> ConversationContext,
    DateTimeOffset CommittedAt,
    InputMode InputMode,
    string? SelectedProvider,
    string? RequestedScope);

/// <summary>Canonical, provider-neutral tool definition. The JSON schema is authoritative and validated locally.</summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonObject ParametersSchema,
    RiskTier Risk,
    bool Mutates);

public sealed record ToolRequest(
    Guid CommandId,
    string StepId,
    string IdempotencyKey,
    int SchemaVersion,
    string ToolName,
    JsonObject Arguments,
    string? ProviderCallId = null);

public sealed record ToolResult(
    ToolOutcome Outcome,
    JsonNode? ObservedState,
    string? ErrorCode,
    string? Message,
    bool Verified,
    bool Retryable,
    IReadOnlyList<string> ChangedResources,
    string? RollbackRef,
    TimeSpan Duration)
{
    public static ToolResult Fail(string code, string message, bool retryable = false) =>
        new(ToolOutcome.Failure, null, code, message, false, retryable, [], null, TimeSpan.Zero);

    public static ToolResult Ok(JsonNode? state, string message, bool verified, IReadOnlyList<string>? changed = null, string? rollbackRef = null) =>
        new(ToolOutcome.Success, state, null, message, verified, false, changed ?? [], rollbackRef, TimeSpan.Zero);

    /// <summary>Compact JSON returned to the reasoning provider. Never contains secrets or authorization data.</summary>
    public JsonObject ToProviderJson()
    {
        var o = new JsonObject
        {
            ["outcome"] = Outcome.ToString().ToLowerInvariant(),
            ["verified"] = Verified,
        };
        if (Message is not null) o["message"] = Message;
        if (ErrorCode is not null) o["error"] = ErrorCode;
        if (ObservedState is not null) o["state"] = ObservedState.DeepClone();
        if (ChangedResources.Count > 0) o["changedCount"] = ChangedResources.Count;
        return o;
    }
}

/// <summary>A user approval bound to an exact manifest digest. Created only by trusted local UI.</summary>
public sealed record Approval(
    Guid ApprovalId,
    Guid RunId,
    string ManifestDigest,
    string Operation,
    DateTimeOffset ExpiresAt,
    string Evidence);

public sealed record TaskEvent(
    Guid RunId,
    long Sequence,
    long MonotonicTick,
    AgentState State,
    string Phase,
    string Message,
    string? StepId = null,
    int? Completed = null,
    int? Total = null,
    bool CanCancel = true);

/// <summary>A speech recognition segment. Revisions replace interim text; finals are ordered by StartMs/sequence.</summary>
public sealed record TranscriptEvent(
    Guid SessionId,
    string SegmentId,
    long Sequence,
    int Revision,
    bool IsFinal,
    string Text,
    long StartMs,
    long EndMs,
    double? Confidence = null);

/// <summary>Request for user approval of an exact manifest; rendered by trusted local UI only.</summary>
public sealed record ApprovalRequest(
    Guid RunId,
    string ToolName,
    string Operation,
    string ManifestDigest,
    string Title,
    IReadOnlyList<string> Lines,
    RiskTier Risk);

public interface IUserApproval
{
    /// <summary>Returns an approval only after explicit local user interaction, or null if declined/cancelled.</summary>
    Task<Approval?> RequestAsync(ApprovalRequest request, CancellationToken ct);
}
