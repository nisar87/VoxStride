using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Events;
using VoxStride.Core.Json;

namespace VoxStride.Core.Execution;

/// <summary>Standing, user-configured local rules that may pre-authorize narrowly scoped reversible operations.</summary>
public interface IStandingRules
{
    bool Covers(ApprovalRequest request, out string ruleId);
}

public sealed class NoStandingRules : IStandingRules
{
    public bool Covers(ApprovalRequest request, out string ruleId) { ruleId = ""; return false; }
}

/// <summary>Operations that a standing rule may ever cover (spec §8: reversible same-root moves only).</summary>
public static class StandingRuleOperations
{
    public const string ReversibleSameRootMove = "files.reversible_same_root_move";
}

/// <summary>
/// The only path from a model/tool request to an OS effect. Enforces, in order: active run generation → tool
/// registered → schema → allowed tool → local risk → semantic validation/scope → approval → journal intent →
/// cancellation recheck → execute → independent verify → journal outcome.
/// </summary>
public sealed class ToolBroker(
    ToolRegistry registry,
    RunAuthority authority,
    IExecutionJournal journal,
    IUserApproval approvals,
    IStandingRules standingRules,
    IEventSink events,
    TimeProvider time)
{
    private readonly ConcurrentDictionary<string, byte> _dispatchedKeys = new(StringComparer.Ordinal);
    public TimeSpan DefaultToolTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public event Action<string, string>? Blocked; // (tool, reason) redacted audit

    public async Task<ToolResult> DispatchAsync(ToolRequest request, RunAuthorization auth, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var status = authority.Check(auth);
        if (status != AuthorizationStatus.Valid) return await BlockAsync(request, auth, "auth_" + status.ToString().ToLowerInvariant(), "Run is not authorized: " + status, ct);

        if (!registry.TryGet(request.ToolName, out var tool)) return await BlockAsync(request, auth, "unknown_tool", "Unsupported tool.", ct);
        if (request.SchemaVersion != 1) return await BlockAsync(request, auth, "schema_version", "Unsupported schema version.", ct);

        var schemaErrors = SchemaValidator.Validate(request.Arguments, tool.Definition.ParametersSchema);
        if (schemaErrors.Count > 0) return await BlockAsync(request, auth, "schema_invalid", string.Join("; ", schemaErrors.Take(5)), ct);

        if (!auth.AllowedTools.Contains(request.ToolName)) return await BlockAsync(request, auth, "tool_not_allowed", "Tool not allowed for this run.", ct);
        if (tool.Definition.Risk >= RiskTier.Prohibited) return await BlockAsync(request, auth, "prohibited", "Prohibited operation.", ct);

        var dedupeKey = auth.RunId + ":" + request.IdempotencyKey;
        if (tool.Definition.Mutates && !_dispatchedKeys.TryAdd(dedupeKey, 0))
            return await BlockAsync(request, auth, "duplicate", "Duplicate side-effect request was not repeated.", ct);

        var ctx = new ToolContext(auth, journal, events, request.StepId, () => authority.Check(auth) == AuthorizationStatus.Valid);
        ToolValidation validation;
        try { validation = await tool.ValidateAsync(request.Arguments, ctx, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("tool", $"{request.StepId} {request.ToolName} validation threw", ex);
            return await BlockAsync(request, auth, "validation_error", ex.GetType().Name, ct);
        }
        if (!validation.Ok)
        {
            Log.Warn("tool", $"{request.StepId} {request.ToolName} validation failed code={validation.ErrorCode ?? "invalid"} message={validation.Message}");
            return Finish(ToolResult.Fail(validation.ErrorCode ?? "invalid", validation.Message ?? "Invalid request."), sw);
        }

        // Local risk policy: Moderate/High mutating tools require manifest-bound approval unless a narrow standing rule covers it.
        var needsApproval = validation.ApprovalNeeded is not null ||
                            (tool.Definition.Mutates && tool.Definition.Risk >= RiskTier.Moderate);
        if (needsApproval)
        {
            var req = validation.ApprovalNeeded;
            if (req is null) return await BlockAsync(request, auth, "approval_manifest_missing", "Tool did not produce an approval manifest.", ct);
            if (req.RunId != auth.RunId) return await BlockAsync(request, auth, "approval_run_mismatch", "Approval request bound to another run.", ct);

            if (req.Operation == StandingRuleOperations.ReversibleSameRootMove && req.Risk <= RiskTier.Moderate && standingRules.Covers(req, out var ruleId))
            {
                ctx.Approval = new Approval(Guid.NewGuid(), auth.RunId, req.ManifestDigest, req.Operation, time.GetUtcNow().AddMinutes(5), "standing-rule:" + ruleId);
                Log.Info("tool", $"{request.StepId} {request.ToolName} approved by standing rule {ruleId} op={req.Operation} risk={req.Risk}");
                events.Publish(auth.RunId, AgentState.Executing, "approval", $"Covered by your saved rule: {req.Title}", request.StepId);
            }
            else
            {
                events.Publish(auth.RunId, AgentState.AwaitingApproval, "approval", req.Title, request.StepId);
                Log.Info("tool", $"{request.StepId} {request.ToolName} waiting for approval op={req.Operation} risk={req.Risk} title={Log.Text(req.Title, 200)}");
                var waitSw = Stopwatch.StartNew();
                var approval = await approvals.RequestAsync(req, ct);
                Log.Info("tool", $"{request.StepId} {request.ToolName} approval {(approval is null ? "declined/expired" : "granted")} after {waitSw.Elapsed.TotalSeconds:0.0}s");
                if (approval is null) return await BlockAsync(request, auth, "approval_declined", "You declined or the approval expired. Nothing was changed.", ct, logBlocked: false);
                if (approval.RunId != auth.RunId || approval.ManifestDigest != req.ManifestDigest || approval.Operation != req.Operation || approval.ExpiresAt < time.GetUtcNow())
                    return await BlockAsync(request, auth, "approval_mismatch", "Approval does not match the exact manifest.", ct);
                ctx.Approval = approval;
                if (authority.Check(auth) != AuthorizationStatus.Valid) return await BlockAsync(request, auth, "auth_revoked", "Run was cancelled while waiting for approval.", ct);
            }
        }

        var redacted = Redact(request.Arguments);
        await journal.RecordAsync(new JournalEntry(Guid.NewGuid(), auth.RunId, request.StepId, request.IdempotencyKey, request.ToolName, redacted, JournalStatus.Intent, null, time.GetUtcNow()), CancellationToken.None);

        ToolResult result;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DefaultToolTimeout);
        try
        {
            if (tool.Definition.Mutates) ctx.EnsureCanWrite(timeout.Token);
            result = await tool.ExecuteAsync(request.Arguments, ctx, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            Log.Warn("tool", $"{request.StepId} {request.ToolName} timed out after {DefaultToolTimeout.TotalSeconds:0}s");
            result = new ToolResult(tool.Definition.Mutates ? ToolOutcome.Unknown : ToolOutcome.Failure, null, "timeout", "The operation timed out; its effect will be reconciled, not retried.", false, !tool.Definition.Mutates, [], null, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            result = new ToolResult(tool.Definition.Mutates ? ToolOutcome.Unknown : ToolOutcome.Failure, null, "cancelled", "Cancelled. Completed effects (if any) are reported in history.", false, false, [], null, sw.Elapsed);
        }
        catch (Exception ex)
        {
            Log.Error("tool", $"{request.StepId} {request.ToolName} threw", ex);
            result = new ToolResult(tool.Definition.Mutates ? ToolOutcome.Unknown : ToolOutcome.Failure, null, "tool_exception", ex.GetType().Name + ": " + ex.Message, false, false, [], null, sw.Elapsed);
        }

        if (result.Outcome is ToolOutcome.Success or ToolOutcome.Partial)
        {
            try
            {
                var v = await tool.VerifyAsync(request.Arguments, result, ctx, CancellationToken.None);
                Log.Write(v.Verified ? LogLevel.Debug : LogLevel.Warn, "tool", $"{request.StepId} {request.ToolName} verify verified={v.Verified} outcome={v.Outcome} message={Log.Text(v.Message ?? "", 200)}", null);
                result = result with { Verified = v.Verified, Outcome = v.Outcome, Message = v.Message ?? result.Message };
            }
            catch (Exception ex)
            {
                Log.Error("tool", $"{request.StepId} {request.ToolName} verification threw", ex);
                result = result with { Verified = false, Outcome = ToolOutcome.Unknown, Message = "Verification failed: " + ex.GetType().Name };
            }
        }

        var js = result.Outcome switch
        {
            ToolOutcome.Success or ToolOutcome.Partial => JournalStatus.Succeeded,
            ToolOutcome.Unknown => JournalStatus.Unknown,
            _ => JournalStatus.Failed,
        };
        await journal.RecordAsync(new JournalEntry(Guid.NewGuid(), auth.RunId, request.StepId, request.IdempotencyKey, request.ToolName, redacted, js, result.ErrorCode ?? result.Outcome.ToString(), time.GetUtcNow()), CancellationToken.None);
        return Finish(result, sw);
    }

    private static ToolResult Finish(ToolResult r, Stopwatch sw) => r with { Duration = sw.Elapsed };

    private async Task<ToolResult> BlockAsync(ToolRequest req, RunAuthorization? auth, string code, string message, CancellationToken ct, bool logBlocked = true)
    {
        Log.Warn("tool", $"{req.StepId} {req.ToolName} BLOCKED code={code} reason={message}");
        if (logBlocked) Blocked?.Invoke(req.ToolName, code);
        await journal.RecordAsync(new JournalEntry(Guid.NewGuid(), auth?.RunId ?? Guid.Empty, req.StepId, req.IdempotencyKey, req.ToolName, "{}", JournalStatus.Blocked, code, time.GetUtcNow()), CancellationToken.None);
        return ToolResult.Fail(code, message);
    }

    /// <summary>Redacts argument values for the journal: keeps keys and short non-secret scalars only.</summary>
    internal static string Redact(JsonObject args)
    {
        var o = new JsonObject();
        foreach (var (k, v) in args)
        {
            if (k.Contains("key", StringComparison.OrdinalIgnoreCase) || k.Contains("token", StringComparison.OrdinalIgnoreCase) || k.Contains("password", StringComparison.OrdinalIgnoreCase))
                o[k] = "[redacted]";
            else if (v is JsonValue jv && jv.ToJsonString().Length <= 80) o[k] = v.DeepClone();
            else o[k] = "[" + (v?.GetValueKind().ToString() ?? "null") + "]";
        }
        return o.ToJsonString();
    }
}
