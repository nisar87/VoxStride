using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Events;

namespace VoxStride.Core.Execution;

public sealed record ToolValidation(bool Ok, string? ErrorCode = null, string? Message = null, ApprovalRequest? ApprovalNeeded = null)
{
    public static ToolValidation Valid() => new(true);
    public static ToolValidation Invalid(string code, string message) => new(false, code, message);
    public static ToolValidation NeedsApproval(ApprovalRequest req) => new(true, ApprovalNeeded: req);
}

public sealed record ToolVerification(bool Verified, ToolOutcome Outcome, string? Message = null);

/// <summary>Context passed to tools for a single dispatch. Tools must call <see cref="EnsureCanWrite"/> before each individual write.</summary>
public sealed class ToolContext
{
    private readonly Func<bool> _canWrite;
    internal ToolContext(RunAuthorization auth, IExecutionJournal journal, IEventSink events, string stepId, Func<bool> canWrite)
    {
        Auth = auth; Journal = journal; Events = events; StepId = stepId; _canWrite = canWrite;
    }
    public RunAuthorization Auth { get; }
    public IExecutionJournal Journal { get; }
    public IEventSink Events { get; }
    public string StepId { get; }
    /// <summary>Set by the broker once a manifest-bound approval or a standing local rule covers this step.</summary>
    public Approval? Approval { get; internal set; }
    /// <summary>Per-dispatch scratch state shared between Validate/Execute/Verify of the same request.</summary>
    public Dictionary<string, object> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>Rechecks cancellation/run generation immediately before a side effect.</summary>
    public void EnsureCanWrite(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_canWrite()) throw new OperationCanceledException("Run authorization revoked before write.");
    }
}

public interface ITool
{
    ToolDefinition Definition { get; }
    ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext context, CancellationToken ct);
    ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct);
    /// <summary>Independently re-observes state. Never trusts the execute result's own claims.</summary>
    ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext context, CancellationToken ct);
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public ToolRegistry Add(ITool tool)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(tool.Definition.Name, "^[a-z][a-z0-9_]{2,63}$"))
            throw new ArgumentException($"Invalid tool name '{tool.Definition.Name}'.");
        if (tool.Definition.Risk == RiskTier.Prohibited) throw new ArgumentException("Prohibited tools cannot be registered.");
        _tools.Add(tool.Definition.Name, tool);
        return this;
    }

    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);
    public IReadOnlyList<ToolDefinition> Definitions => _tools.Values.Select(t => t.Definition).ToList();
    public IReadOnlyList<ToolDefinition> Select(IEnumerable<string> names) =>
        names.Where(_tools.ContainsKey).Select(n => _tools[n].Definition).ToList();
}
