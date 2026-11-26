using VoxStride.Contracts;
using VoxStride.Core.Agent;
using VoxStride.Core.Execution;
using VoxStride.Persistence;
using Xunit;

namespace VoxStride.Tests.Persistence;

public sealed class SqliteStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "voxstride-db-" + Guid.NewGuid().ToString("N")[..8] + ".db");
    private readonly SqliteStore _s;
    public SqliteStoreTests() { _s = new SqliteStore(_path); }
    public void Dispose()
    {
        _s.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) { try { File.Delete(f); } catch (IOException) { } }
    }

    [Fact]
    public async Task JournalReportsUnresolvedByLatestStatusPerKey()
    {
        var run = Guid.NewGuid();
        await _s.RecordAsync(new JournalEntry(Guid.NewGuid(), run, "s1", "k1", "t", "{}", JournalStatus.Intent, null, DateTimeOffset.UtcNow), default);
        await _s.RecordAsync(new JournalEntry(Guid.NewGuid(), run, "s1", "k1", "t", "{}", JournalStatus.Succeeded, null, DateTimeOffset.UtcNow), default);
        await _s.RecordAsync(new JournalEntry(Guid.NewGuid(), run, "s2", "k2", "t", "{}", JournalStatus.Intent, null, DateTimeOffset.UtcNow), default);
        var unresolved = await _s.GetUnresolvedAsync(default);
        Assert.Equal("k2", Assert.Single(unresolved).IdempotencyKey);
        Assert.Equal(3, (await _s.GetRunAsync(run, default)).Count);
    }

    [Fact]
    public async Task MovesUpsertKeepOrderAndSurviveReopen()
    {
        var id = Guid.NewGuid();
        var m = new MoveRecord(id, Guid.NewGuid(), "m-aaaaaaaaaaaa", "C:\\a.pdf", "C:\\PDFs\\a.pdf", 10, DateTime.UtcNow, "intent", DateTimeOffset.UtcNow);
        await _s.RecordMoveAsync(m, default);
        Assert.Single(await _s.GetUnresolvedMovesAsync(default));
        await _s.RecordMoveAsync(m with { Status = "moved" }, default);
        await _s.RecordMoveAsync(m with { MoveId = Guid.NewGuid(), SourcePath = "C:\\b.pdf", Status = "moved" }, default);
        Assert.Empty(await _s.GetUnresolvedMovesAsync(default));
        using var reopened = new SqliteStore(_path);
        var moves = await reopened.GetMovesAsync("m-aaaaaaaaaaaa", default);
        Assert.Equal(2, moves.Count);
        Assert.Equal("C:\\a.pdf", moves[0].SourcePath);
        Assert.Equal(m.LastWriteUtc, moves[0].LastWriteUtc);
        Assert.Equal("m-aaaaaaaaaaaa", await reopened.GetLastUndoableManifestAsync(default));
    }

    [Fact]
    public async Task InvalidMoveStatusIsRejectedByConstraint()
    {
        var m = new MoveRecord(Guid.NewGuid(), Guid.NewGuid(), "m-x", "a", "b", 1, DateTime.UtcNow, "bogus", DateTimeOffset.UtcNow);
        await Assert.ThrowsAnyAsync<Exception>(() => _s.RecordMoveAsync(m, default));
    }

    [Fact]
    public async Task HistoryOmitsTranscriptUnlessRetentionEnabled()
    {
        var cmd = new CommandEnvelope(Guid.NewGuid(), Guid.NewGuid(), "organize my secret project folder", RunAuthority.HashTranscript("organize my secret project folder"), "en", [], DateTimeOffset.UtcNow, InputMode.Voice, "Claude", null);
        var run = new RunRecord(Guid.NewGuid(), cmd, "Cloud", "Claude", "m", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, "Done.",
            [new StepRecord("s1", "desktop_preview_organization", ToolOutcome.Success, true, "ok", 12)], 100, 20, new Dictionary<string, double> { ["t_ui_ack"] = 3 });
        await _s.SaveAsync(run, default);
        var h = Assert.Single(await _s.GetHistoryAsync(10, default));
        Assert.Null(h.Transcript);
        Assert.Single(h.Steps);
        var (i, o) = await _s.GetUsageSinceAsync(DateTimeOffset.UtcNow.AddHours(-1), "Claude", default);
        Assert.Equal(100, i); Assert.Equal(20, o);

        _s.RetainTranscripts = true;
        await _s.SaveAsync(run with { RunId = Guid.NewGuid() }, default);
        Assert.Contains(await _s.GetHistoryAsync(10, default), x => x.Transcript == "organize my secret project folder");

        await _s.ClearHistoryAsync(false, default);
        Assert.Empty(await _s.GetHistoryAsync(10, default));
    }

    [Fact]
    public async Task StandingRulesCoverOnlyExactFolderAndReversibleMoves()
    {
        await _s.AddRuleAsync(StandingRuleOperations.ReversibleSameRootMove, "C:\\Users\\me\\Desktop", default);
        var rules = new StoredStandingRules(_s);
        await rules.RefreshAsync(default);
        ApprovalRequest Req(string op, string? scope) => new(Guid.NewGuid(), "t", op, "d", "title", [], RiskTier.Moderate, scope);
        Assert.True(rules.Covers(Req(StandingRuleOperations.ReversibleSameRootMove, "C:\\Users\\me\\Desktop"), out _));
        Assert.False(rules.Covers(Req(StandingRuleOperations.ReversibleSameRootMove, "C:\\Users\\me\\Downloads"), out _));
        Assert.False(rules.Covers(Req("files.cleanup_temp_permanent", "C:\\Users\\me\\Desktop"), out _));
        await _s.RevokeAllRulesAsync(default);
        await rules.RefreshAsync(default);
        Assert.False(rules.Covers(Req(StandingRuleOperations.ReversibleSameRootMove, "C:\\Users\\me\\Desktop"), out _));
    }
}
