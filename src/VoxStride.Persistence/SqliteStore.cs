using System.Text.Json;
using Microsoft.Data.Sqlite;
using VoxStride.Contracts;
using VoxStride.Core.Agent;
using VoxStride.Core.Execution;

namespace VoxStride.Persistence;

/// <summary>
/// Per-user local SQLite store for the execution journal, reversible move manifests, run history and standing
/// permission rules. Raw audio, screenshots, provider prompts and secrets are never stored. Transcripts are
/// stored only when the user enables history retention.
/// </summary>
public sealed class SqliteStore : IExecutionJournal, IRunHistory, IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _cs;
    private readonly SemaphoreSlim _write = new(1, 1);

    public bool RetainTranscripts { get; set; }

    public SqliteStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = true }.ToString();
        Migrate();
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxStride", "voxstride.db");

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000;";
        p.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using var wal = c.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        wal.ExecuteNonQuery();
        using var v = c.CreateCommand();
        v.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(v.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (version >= SchemaVersion) return;
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ToolEvents(
              EntryId TEXT PRIMARY KEY, RunId TEXT NOT NULL, StepId TEXT NOT NULL, IdempotencyKey TEXT NOT NULL,
              ToolName TEXT NOT NULL, RedactedArguments TEXT NOT NULL, Status TEXT NOT NULL, Detail TEXT, At TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_ToolEvents_Run ON ToolEvents(RunId);
            CREATE INDEX IF NOT EXISTS IX_ToolEvents_Key ON ToolEvents(IdempotencyKey, At);
            CREATE TABLE IF NOT EXISTS FileMoveManifest(
              MoveId TEXT PRIMARY KEY, RunId TEXT NOT NULL, ManifestId TEXT NOT NULL, SourcePath TEXT NOT NULL, DestinationPath TEXT NOT NULL,
              Length INTEGER NOT NULL, LastWriteUtcTicks INTEGER NOT NULL, Status TEXT NOT NULL CHECK(Status IN ('intent','moved','failed','unknown','undone','undo-conflict')),
              At TEXT NOT NULL, Seq INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Moves_Manifest ON FileMoveManifest(ManifestId, Seq);
            CREATE TABLE IF NOT EXISTS RollbackRecords(Id INTEGER PRIMARY KEY AUTOINCREMENT, RunId TEXT NOT NULL, ManifestId TEXT NOT NULL, Kind TEXT NOT NULL, Path TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Commands(
              RunId TEXT PRIMARY KEY, CommandId TEXT NOT NULL, SessionId TEXT NOT NULL, Transcript TEXT, TranscriptHash TEXT NOT NULL,
              InputMode TEXT NOT NULL, Route TEXT NOT NULL, Provider TEXT, Model TEXT, StartedAt TEXT NOT NULL, EndedAt TEXT,
              Success INTEGER NOT NULL, Summary TEXT NOT NULL, InputTokens INTEGER NOT NULL, OutputTokens INTEGER NOT NULL, TimingsJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Commands_Started ON Commands(StartedAt);
            CREATE TABLE IF NOT EXISTS PlanSteps(
              RunId TEXT NOT NULL REFERENCES Commands(RunId) ON DELETE CASCADE, StepId TEXT NOT NULL, Tool TEXT NOT NULL, Outcome TEXT NOT NULL,
              Verified INTEGER NOT NULL, Message TEXT NOT NULL, DurationMs REAL NOT NULL, PRIMARY KEY(RunId, StepId));
            CREATE TABLE IF NOT EXISTS UsageEvents(Id INTEGER PRIMARY KEY AUTOINCREMENT, RunId TEXT NOT NULL, Provider TEXT NOT NULL, Model TEXT, InputTokens INTEGER NOT NULL, OutputTokens INTEGER NOT NULL, At TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS PermissionRules(
              RuleId TEXT PRIMARY KEY, Operation TEXT NOT NULL, Scope TEXT NOT NULL, CreatedAt TEXT NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1);
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"PRAGMA user_version={SchemaVersion};";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private async Task ExecAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var c = Open();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _write.Release(); }
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Action<SqliteCommand> bind, Func<SqliteDataReader, T> map, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        var list = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(map(r));
        return list;
    }

    private static string Iso(DateTimeOffset d) => d.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    // ---- IExecutionJournal ----
    public Task RecordAsync(JournalEntry e, CancellationToken ct) => ExecAsync(
        "INSERT INTO ToolEvents VALUES($id,$run,$step,$key,$tool,$args,$status,$detail,$at)", c =>
        {
            c.Parameters.AddWithValue("$id", e.EntryId.ToString()); c.Parameters.AddWithValue("$run", e.RunId.ToString());
            c.Parameters.AddWithValue("$step", e.StepId); c.Parameters.AddWithValue("$key", e.IdempotencyKey);
            c.Parameters.AddWithValue("$tool", e.ToolName); c.Parameters.AddWithValue("$args", e.RedactedArguments);
            c.Parameters.AddWithValue("$status", e.Status.ToString()); c.Parameters.AddWithValue("$detail", (object?)e.Detail ?? DBNull.Value);
            c.Parameters.AddWithValue("$at", Iso(e.At));
        }, ct);

    private static JournalEntry MapEntry(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        Enum.Parse<JournalStatus>(r.GetString(6)), r.IsDBNull(7) ? null : r.GetString(7), ParseIso(r.GetString(8)));

    public async Task<IReadOnlyList<JournalEntry>> GetRunAsync(Guid runId, CancellationToken ct) =>
        await QueryAsync("SELECT * FROM ToolEvents WHERE RunId=$r ORDER BY At, rowid", c => c.Parameters.AddWithValue("$r", runId.ToString()), MapEntry, ct);

    public async Task<IReadOnlyList<JournalEntry>> GetUnresolvedAsync(CancellationToken ct) =>
        await QueryAsync("""
            SELECT t.* FROM ToolEvents t
            WHERE t.rowid = (SELECT MAX(rowid) FROM ToolEvents x WHERE x.IdempotencyKey = t.IdempotencyKey)
              AND t.Status IN ('Intent','Unknown')
            """, _ => { }, MapEntry, ct);

    public Task RecordMoveAsync(MoveRecord m, CancellationToken ct) => ExecAsync(
        """
        INSERT INTO FileMoveManifest VALUES($id,$run,$man,$src,$dst,$len,$lw,$st,$at,(SELECT COALESCE(MAX(Seq),0)+1 FROM FileMoveManifest))
        ON CONFLICT(MoveId) DO UPDATE SET Status=excluded.Status, At=excluded.At
        """, c =>
        {
            c.Parameters.AddWithValue("$id", m.MoveId.ToString()); c.Parameters.AddWithValue("$run", m.RunId.ToString());
            c.Parameters.AddWithValue("$man", m.ManifestId); c.Parameters.AddWithValue("$src", m.SourcePath); c.Parameters.AddWithValue("$dst", m.DestinationPath);
            c.Parameters.AddWithValue("$len", m.Length); c.Parameters.AddWithValue("$lw", m.LastWriteUtc.Ticks);
            c.Parameters.AddWithValue("$st", m.Status); c.Parameters.AddWithValue("$at", Iso(m.At));
        }, ct);

    private static MoveRecord MapMove(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetInt64(5), new DateTime(r.GetInt64(6), DateTimeKind.Utc), r.GetString(7), ParseIso(r.GetString(8)));

    public async Task<IReadOnlyList<MoveRecord>> GetMovesAsync(string manifestId, CancellationToken ct) =>
        await QueryAsync("SELECT * FROM FileMoveManifest WHERE ManifestId=$m ORDER BY Seq", c => c.Parameters.AddWithValue("$m", manifestId), MapMove, ct);

    public async Task<IReadOnlyList<MoveRecord>> GetUnresolvedMovesAsync(CancellationToken ct) =>
        await QueryAsync("SELECT * FROM FileMoveManifest WHERE Status IN ('intent','unknown') ORDER BY Seq", _ => { }, MapMove, ct);

    public Task RecordCreatedFolderAsync(CreatedFolderRecord f, CancellationToken ct) => ExecAsync(
        "INSERT INTO RollbackRecords(RunId,ManifestId,Kind,Path) VALUES($r,$m,'created-folder',$p)", c =>
        {
            c.Parameters.AddWithValue("$r", f.RunId.ToString()); c.Parameters.AddWithValue("$m", f.ManifestId); c.Parameters.AddWithValue("$p", f.Path);
        }, ct);

    public async Task<IReadOnlyList<CreatedFolderRecord>> GetCreatedFoldersAsync(string manifestId, CancellationToken ct) =>
        await QueryAsync("SELECT RunId, ManifestId, Path FROM RollbackRecords WHERE ManifestId=$m AND Kind='created-folder' ORDER BY Id",
            c => c.Parameters.AddWithValue("$m", manifestId), r => new CreatedFolderRecord(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2)), ct);

    public async Task<string?> GetLastUndoableManifestAsync(CancellationToken ct) =>
        (await QueryAsync("SELECT ManifestId FROM FileMoveManifest WHERE Status='moved' ORDER BY Seq DESC LIMIT 1", _ => { }, r => r.GetString(0), ct)).FirstOrDefault();

    // ---- IRunHistory ----
    public async Task SaveAsync(RunRecord run, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var c = Open();
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO Commands VALUES($run,$cmd,$sess,$tr,$hash,$mode,$route,$prov,$model,$start,$end,$ok,$sum,$in,$out,$tim)";
                cmd.Parameters.AddWithValue("$run", run.RunId.ToString());
                cmd.Parameters.AddWithValue("$cmd", run.Command.CommandId.ToString());
                cmd.Parameters.AddWithValue("$sess", run.Command.SessionId.ToString());
                cmd.Parameters.AddWithValue("$tr", RetainTranscripts ? run.Command.FinalizedTranscript : DBNull.Value);
                cmd.Parameters.AddWithValue("$hash", run.Command.TranscriptHash);
                cmd.Parameters.AddWithValue("$mode", run.Command.InputMode.ToString());
                cmd.Parameters.AddWithValue("$route", run.Route);
                cmd.Parameters.AddWithValue("$prov", (object?)run.Provider ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$model", (object?)run.Model ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$start", Iso(run.StartedAt));
                cmd.Parameters.AddWithValue("$end", run.EndedAt is { } e ? Iso(e) : DBNull.Value);
                cmd.Parameters.AddWithValue("$ok", run.Success ? 1 : 0);
                cmd.Parameters.AddWithValue("$sum", run.Summary);
                cmd.Parameters.AddWithValue("$in", run.InputTokens);
                cmd.Parameters.AddWithValue("$out", run.OutputTokens);
                cmd.Parameters.AddWithValue("$tim", JsonSerializer.Serialize(run.TimingsMs));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            foreach (var s in run.Steps)
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO PlanSteps VALUES($run,$id,$tool,$out,$ver,$msg,$dur)";
                cmd.Parameters.AddWithValue("$run", run.RunId.ToString()); cmd.Parameters.AddWithValue("$id", s.StepId);
                cmd.Parameters.AddWithValue("$tool", s.Tool); cmd.Parameters.AddWithValue("$out", s.Outcome.ToString());
                cmd.Parameters.AddWithValue("$ver", s.Verified ? 1 : 0); cmd.Parameters.AddWithValue("$msg", s.Message);
                cmd.Parameters.AddWithValue("$dur", s.DurationMs);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            if (run.Provider is not null && run.InputTokens + run.OutputTokens > 0)
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO UsageEvents(RunId,Provider,Model,InputTokens,OutputTokens,At) VALUES($r,$p,$m,$i,$o,$a)";
                cmd.Parameters.AddWithValue("$r", run.RunId.ToString()); cmd.Parameters.AddWithValue("$p", run.Provider);
                cmd.Parameters.AddWithValue("$m", (object?)run.Model ?? DBNull.Value); cmd.Parameters.AddWithValue("$i", run.InputTokens);
                cmd.Parameters.AddWithValue("$o", run.OutputTokens); cmd.Parameters.AddWithValue("$a", Iso(run.EndedAt ?? run.StartedAt));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { _write.Release(); }
    }

    public sealed record HistoryItem(Guid RunId, DateTimeOffset StartedAt, string? Transcript, string Route, string? Provider, bool Success, string Summary, long InputTokens, long OutputTokens, IReadOnlyList<StepRecord> Steps);

    public async Task<IReadOnlyList<HistoryItem>> GetHistoryAsync(int limit, CancellationToken ct)
    {
        var runs = await QueryAsync("SELECT RunId, StartedAt, Transcript, Route, Provider, Success, Summary, InputTokens, OutputTokens FROM Commands ORDER BY StartedAt DESC LIMIT $l",
            c => c.Parameters.AddWithValue("$l", limit),
            r => (Id: Guid.Parse(r.GetString(0)), At: ParseIso(r.GetString(1)), Tr: r.IsDBNull(2) ? null : r.GetString(2), Route: r.GetString(3), Prov: r.IsDBNull(4) ? null : r.GetString(4), Ok: r.GetInt32(5) == 1, Sum: r.GetString(6), In: r.GetInt64(7), Out: r.GetInt64(8)), ct);
        var list = new List<HistoryItem>();
        foreach (var x in runs)
        {
            var steps = await QueryAsync("SELECT StepId, Tool, Outcome, Verified, Message, DurationMs FROM PlanSteps WHERE RunId=$r ORDER BY rowid",
                c => c.Parameters.AddWithValue("$r", x.Id.ToString()),
                r => new StepRecord(r.GetString(0), r.GetString(1), Enum.Parse<ToolOutcome>(r.GetString(2)), r.GetInt32(3) == 1, r.GetString(4), r.GetDouble(5)), ct);
            list.Add(new HistoryItem(x.Id, x.At, x.Tr, x.Route, x.Prov, x.Ok, x.Sum, x.In, x.Out, steps));
        }
        return list;
    }

    public async Task<(long input, long output)> GetUsageSinceAsync(DateTimeOffset since, string provider, CancellationToken ct)
    {
        var r = await QueryAsync("SELECT COALESCE(SUM(InputTokens),0), COALESCE(SUM(OutputTokens),0) FROM UsageEvents WHERE At >= $s AND Provider=$p",
            c => { c.Parameters.AddWithValue("$s", Iso(since)); c.Parameters.AddWithValue("$p", provider); }, x => (x.GetInt64(0), x.GetInt64(1)), ct);
        return r[0];
    }

    /// <summary>Deletes history and usage. Undo data for moves is kept unless <paramref name="includeUndoData"/>.</summary>
    public async Task ClearHistoryAsync(bool includeUndoData, CancellationToken ct)
    {
        await ExecAsync("DELETE FROM PlanSteps; DELETE FROM Commands; DELETE FROM UsageEvents; DELETE FROM ToolEvents;" + (includeUndoData ? " DELETE FROM FileMoveManifest; DELETE FROM RollbackRecords;" : ""), _ => { }, ct);
        await ExecAsync("VACUUM;", _ => { }, ct);
    }

    // ---- Standing permission rules ----
    public sealed record PermissionRule(string RuleId, string Operation, string Scope, DateTimeOffset CreatedAt, bool Enabled);

    public Task AddRuleAsync(string operation, string scope, CancellationToken ct) => ExecAsync(
        "INSERT INTO PermissionRules VALUES($id,$op,$scope,$at,1)", c =>
        {
            c.Parameters.AddWithValue("$id", "r-" + Guid.NewGuid().ToString("N")[..10]); c.Parameters.AddWithValue("$op", operation);
            c.Parameters.AddWithValue("$scope", scope); c.Parameters.AddWithValue("$at", Iso(DateTimeOffset.UtcNow));
        }, ct);

    public async Task<IReadOnlyList<PermissionRule>> GetRulesAsync(CancellationToken ct) =>
        await QueryAsync("SELECT RuleId, Operation, Scope, CreatedAt, Enabled FROM PermissionRules ORDER BY CreatedAt", _ => { },
            r => new PermissionRule(r.GetString(0), r.GetString(1), r.GetString(2), ParseIso(r.GetString(3)), r.GetInt32(4) == 1), ct);

    public Task RevokeAllRulesAsync(CancellationToken ct) => ExecAsync("DELETE FROM PermissionRules", _ => { }, ct);
    public Task RevokeRuleAsync(string id, CancellationToken ct) => ExecAsync("DELETE FROM PermissionRules WHERE RuleId=$id", c => c.Parameters.AddWithValue("$id", id), ct);

    public void Dispose()
    {
        _write.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

/// <summary>
/// Standing rules backed by the store. Only <see cref="StandingRuleOperations.ReversibleSameRootMove"/> may be
/// covered, and only for the exact folder root recorded in the rule scope.
/// </summary>
public sealed class StoredStandingRules(SqliteStore store) : IStandingRules
{
    private volatile IReadOnlyList<SqliteStore.PermissionRule> _cache = [];

    public async Task RefreshAsync(CancellationToken ct) => _cache = await store.GetRulesAsync(ct);

    public bool Covers(ApprovalRequest request, out string ruleId)
    {
        ruleId = "";
        if (request.Operation != StandingRuleOperations.ReversibleSameRootMove || request.Risk > RiskTier.Moderate) return false;
        var root = request.Scope;
        if (string.IsNullOrEmpty(root)) return false;
        var rule = _cache.FirstOrDefault(r => r.Enabled && r.Operation == request.Operation && string.Equals(r.Scope, root, StringComparison.OrdinalIgnoreCase));
        if (rule is null) return false;
        ruleId = rule.RuleId;
        return true;
    }
}
