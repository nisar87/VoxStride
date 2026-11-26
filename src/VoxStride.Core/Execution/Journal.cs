using System.Collections.Concurrent;
using VoxStride.Contracts;

namespace VoxStride.Core.Execution;

public enum JournalStatus { Intent, Succeeded, Failed, Unknown, Blocked }

public sealed record JournalEntry(
    Guid EntryId,
    Guid RunId,
    string StepId,
    string IdempotencyKey,
    string ToolName,
    string RedactedArguments,
    JournalStatus Status,
    string? Detail,
    DateTimeOffset At);

/// <summary>A reversible file move recorded before and after execution.</summary>
public sealed record MoveRecord(
    Guid MoveId,
    Guid RunId,
    string ManifestId,
    string SourcePath,
    string DestinationPath,
    long Length,
    DateTime LastWriteUtc,
    string Status, // intent | moved | failed | unknown | undone | undo-conflict
    DateTimeOffset At);

public sealed record CreatedFolderRecord(Guid RunId, string ManifestId, string Path);

/// <summary>Durable execution journal. Every side effect records an intent before and an outcome after.</summary>
public interface IExecutionJournal
{
    Task RecordAsync(JournalEntry entry, CancellationToken ct);
    Task<IReadOnlyList<JournalEntry>> GetRunAsync(Guid runId, CancellationToken ct);
    /// <summary>Entries whose last status is Intent or Unknown — reported on restart, never replayed.</summary>
    Task<IReadOnlyList<JournalEntry>> GetUnresolvedAsync(CancellationToken ct);
    Task RecordMoveAsync(MoveRecord move, CancellationToken ct);
    Task<IReadOnlyList<MoveRecord>> GetMovesAsync(string manifestId, CancellationToken ct);
    Task<IReadOnlyList<MoveRecord>> GetUnresolvedMovesAsync(CancellationToken ct);
    Task RecordCreatedFolderAsync(CreatedFolderRecord folder, CancellationToken ct);
    Task<IReadOnlyList<CreatedFolderRecord>> GetCreatedFoldersAsync(string manifestId, CancellationToken ct);
    Task<string?> GetLastUndoableManifestAsync(CancellationToken ct);
}

/// <summary>In-memory journal used for tests and when persistence is unavailable. Same semantics as SQLite journal.</summary>
public sealed class InMemoryJournal : IExecutionJournal
{
    private readonly ConcurrentQueue<JournalEntry> _entries = new();
    private readonly ConcurrentDictionary<Guid, MoveRecord> _moves = new();
    private readonly ConcurrentQueue<MoveRecord> _moveOrder = new();
    private readonly ConcurrentQueue<CreatedFolderRecord> _folders = new();

    public Task RecordAsync(JournalEntry entry, CancellationToken ct) { _entries.Enqueue(entry); return Task.CompletedTask; }

    public Task<IReadOnlyList<JournalEntry>> GetRunAsync(Guid runId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JournalEntry>>(_entries.Where(e => e.RunId == runId).ToList());

    public Task<IReadOnlyList<JournalEntry>> GetUnresolvedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JournalEntry>>(_entries
            .GroupBy(e => e.IdempotencyKey)
            .Select(g => g.Last())
            .Where(e => e.Status is JournalStatus.Intent or JournalStatus.Unknown)
            .ToList());

    public Task RecordMoveAsync(MoveRecord move, CancellationToken ct)
    {
        if (!_moves.ContainsKey(move.MoveId)) _moveOrder.Enqueue(move);
        _moves[move.MoveId] = move;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MoveRecord>> GetMovesAsync(string manifestId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MoveRecord>>(_moveOrder.Select(m => _moves[m.MoveId]).Where(m => m.ManifestId == manifestId).ToList());

    public Task<IReadOnlyList<MoveRecord>> GetUnresolvedMovesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MoveRecord>>(_moves.Values.Where(m => m.Status is "intent" or "unknown").ToList());

    public Task RecordCreatedFolderAsync(CreatedFolderRecord folder, CancellationToken ct) { _folders.Enqueue(folder); return Task.CompletedTask; }

    public Task<IReadOnlyList<CreatedFolderRecord>> GetCreatedFoldersAsync(string manifestId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CreatedFolderRecord>>(_folders.Where(f => f.ManifestId == manifestId).ToList());

    public Task<string?> GetLastUndoableManifestAsync(CancellationToken ct) =>
        Task.FromResult(_moveOrder.Select(m => _moves[m.MoveId]).LastOrDefault(m => m.Status == "moved")?.ManifestId);
}
