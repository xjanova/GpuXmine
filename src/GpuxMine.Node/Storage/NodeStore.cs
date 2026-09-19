using System.Globalization;

using Microsoft.Data.Sqlite;

namespace GpuxMine.Node.Storage;

/// <summary>
/// The node's local database: settings, the job ledger, and the activity log.
/// </summary>
/// <remarks>
/// <para>
/// Replaces a JSON settings file and two in-memory ring buffers. The buffers
/// were the real problem: a node that ran for a month knew only its last 300
/// jobs and 2,000 log lines, and lost both on restart — so "jobs done today"
/// reset every time the owner rebooted, and a payout dispute had nothing to
/// argue from.
/// </para>
/// <para>
/// SQLite in WAL mode because this is one process with a UI thread reading
/// while a background thread writes. WAL lets those overlap instead of
/// blocking each other, which is the difference between a queue screen that
/// stays live during a render and one that stutters.
/// </para>
/// <para>
/// <b>Money is never stored as a float.</b> Payouts are integer satang; the
/// pool is the authority for the value, and this table is a local mirror for
/// showing the owner what they earned and when.
/// </para>
/// </remarks>
public sealed class NodeStore : IDisposable
{
    /// <summary>Everything older than this is swept nightly — long enough for any dispute, short enough to stay small.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(120);

    private string _connectionString;
    private readonly SqliteConnection _keepAlive;
    private readonly Lock _writeGate = new();

    public string Path { get; private set; }

    /// <summary>Set when the ledger could not be opened and had to be replaced. The host tells the owner.</summary>
    public string? RecoveredFrom { get; private set; }

    /// <summary>
    /// Set when the damaged ledger could not even be moved, and the node is
    /// running from a file beside it instead. Rare, and worth saying out loud:
    /// it means something else still holds the original.
    /// </summary>
    public string? SidesteppedTo { get; private set; }

    public NodeStore(string databasePath)
    {
        Path = databasePath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);
        _connectionString = ConnectionStringFor(databasePath);

        try
        {
            _keepAlive = OpenAndPrepare();
        }
        catch (SqliteException ex)
        {
            // A damaged ledger must not be the end of the node.
            //
            // It was, once: a file left inconsistent by an earlier version's
            // data move threw 'database disk image is malformed' out of this
            // constructor, straight through NodeHost's, and the window died
            // before it opened. The machine was unrecoverable by its owner —
            // no window, no message, and the file to delete was one they had
            // never heard of.
            //
            // Losing history is bad. Being unable to start is worse, and it is
            // the one of the two that also stops the node earning.
            RecoveredFrom = Quarantine(databasePath, ex, out string? openInstead);
            if (openInstead is not null)
            {
                // The damaged file is still there and still held. Start beside
                // it rather than not at all.
                databasePath = openInstead;
                _connectionString = ConnectionStringFor(databasePath);
                SidesteppedTo = databasePath;
                Path = databasePath;
            }
            _keepAlive = OpenAndPrepare();
        }

        Migrate();
    }

    private static string ConnectionStringFor(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

    private SqliteConnection OpenAndPrepare()
    {
        // One connection held open for the life of the node. Without it the
        // last connection closing would delete the WAL and checkpoint on every
        // single write, which is most of the cost of using SQLite badly.
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();

            Execute(connection, """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA foreign_keys = ON;
                """);

            CheckIntegrity(connection);

            return connection;
        }
        catch
        {
            // Without this the failed connection keeps the file open, and the
            // quarantine that is about to run cannot move it — which turned a
            // recoverable corrupt ledger back into a node that will not start.
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Asks the file whether it is sound, while there is still somewhere to put
    /// it if it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opening a corrupt SQLite database usually succeeds — the damage surfaces
    /// later, on whichever statement happens to touch the broken page. That is
    /// how a bad ledger once came through the constructor intact and then threw
    /// out of the first read the window did, killing the app at startup with a
    /// stack trace and no window: past the one place that knows how to recover.
    /// </para>
    /// <para>
    /// <c>integrity_check</c> rather than <c>quick_check</c>, because the damage
    /// seen in practice was indexes disagreeing with their tables, and that is
    /// exactly the class of fault <c>quick_check</c> skips. It is capped at ten
    /// errors: this runs on every start, the node's database is a few hundred
    /// kilobytes, and one error is already enough to condemn the file.
    /// </para>
    /// </remarks>
    private static void CheckIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check(10)";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string line = reader.GetString(0);
            if (!string.Equals(line, "ok", StringComparison.Ordinal))
            {
                // Thrown as the same exception the caller already knows how to
                // answer, so a file that fails here is quarantined by exactly
                // the path a file that would not open at all takes.
                throw new SqliteException($"integrity check failed: {line}", 11);
            }
        }
    }

    /// <summary>
    /// Moves a database we cannot open out of the way, and reports where it went.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted. It is the owner's record of work they did, it
    /// may still be readable by hand, and silently destroying the file that
    /// proves a payout is the wrong instinct even when it is unreadable to us.
    /// </remarks>
    private static string Quarantine(string databasePath, SqliteException cause, out string? openInstead)
    {
        // Invariant, or a Thai machine names the file in the Buddhist era and
        // the owner sends support a node.db.corrupt-2569... that reads as being
        // from the year 2569. The stamp exists to be compared with a date in a
        // log; it has to mean the same thing on every node in the fleet.
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string kept = $"{databasePath}.corrupt-{stamp}";

        SqliteConnection.ClearAllPools();

        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string source = databasePath + suffix;
            if (!File.Exists(source)) continue;
            try
            {
                // The WAL and shm go too: leaving either beside a fresh
                // database is how this happened in the first place.
                File.Move(source, kept + suffix, overwrite: true);
            }
            catch
            {
                try { File.Delete(source); } catch { /* nothing further to try */ }
            }
        }

        // Freed the name: the fresh ledger takes the usual path, and nothing
        // downstream has to know this happened.
        openInstead = File.Exists(databasePath) ? Sidestep(databasePath, stamp) : null;
        return kept;
    }

    /// <summary>
    /// A path we can open when the damaged ledger refuses to move out of the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to throw. The reasoning was that if the bad file is still
    /// sitting there, the fresh database would open on top of the damage — so
    /// better to fail loudly than to pretend. In practice it failed loudly in
    /// the one place that cannot afford it: the exception came out of the
    /// constructor, past the handler written to keep a bad ledger from ending
    /// the node, and the owner got a process that died at startup with no
    /// window and nothing to act on. Recorded on this machine at 09:09 on
    /// 2026-09-19, on a file another copy of the program still had open.
    /// </para>
    /// <para>
    /// A file that will not move is almost always a file something else holds,
    /// and the honest answer to that is to leave it alone and keep our records
    /// somewhere else. History is lost either way; the difference is whether
    /// the machine goes on earning while somebody sorts it out. The server
    /// holds the authoritative record of finished work regardless.
    /// </para>
    /// </remarks>
    private static string Sidestep(string databasePath, string stamp)
    {
        // System.IO.Path spelled out: this type has its own `Path` property.
        string directory = System.IO.Path.GetDirectoryName(databasePath) ?? ".";
        string name = System.IO.Path.GetFileNameWithoutExtension(databasePath);
        string extension = System.IO.Path.GetExtension(databasePath);
        return System.IO.Path.Combine(directory, $"{name}-{stamp}{extension}");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------- schema

    private void Migrate()
    {
        lock (_writeGate)
        {
            using var connection = Open();
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS settings (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS jobs (
                    prompt_id     TEXT PRIMARY KEY,
                    kind          TEXT NOT NULL,
                    status        TEXT NOT NULL,
                    nodes_total   INTEGER NOT NULL DEFAULT 0,
                    -- unix milliseconds: an upscale finishes in a couple of
                    -- seconds, and second-resolution showed every one of them as "0s"
                    submitted_at  INTEGER NOT NULL,
                    started_at    INTEGER,
                    completed_at  INTEGER,
                    output_file   TEXT,
                    error         TEXT,
                    -- integer satang, never a float; NULL until the pool settles it
                    payout_satang INTEGER,
                    free_share    INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS ix_jobs_submitted ON jobs (submitted_at DESC);
                CREATE INDEX IF NOT EXISTS ix_jobs_completed ON jobs (completed_at);

                CREATE TABLE IF NOT EXISTS log (
                    id      INTEGER PRIMARY KEY AUTOINCREMENT,
                    at      INTEGER NOT NULL,
                    channel TEXT NOT NULL,
                    level   INTEGER NOT NULL,
                    message TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_log_at ON log (at DESC);
                CREATE INDEX IF NOT EXISTS ix_log_channel ON log (channel, at DESC);
                """);
        }
    }

    // ------------------------------------------------------------- settings

    public string? GetSetting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", value);
            command.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------- jobs

    public void JobSubmitted(string promptId, string kind, int nodesTotal, bool freeShare)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            // The same prompt id arriving twice is a retry, not a second job.
            command.CommandText = """
                INSERT INTO jobs (prompt_id, kind, status, nodes_total, submitted_at, free_share)
                VALUES ($id, $kind, 'queued', $nodes, $at, $free)
                ON CONFLICT(prompt_id) DO NOTHING
                """;
            command.Parameters.AddWithValue("$id", promptId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$nodes", nodesTotal);
            command.Parameters.AddWithValue("$at", Now());
            command.Parameters.AddWithValue("$free", freeShare ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    public void JobStarted(string promptId)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET status = 'running', started_at = $at WHERE prompt_id = $id AND started_at IS NULL";
            command.Parameters.AddWithValue("$id", promptId);
            command.Parameters.AddWithValue("$at", Now());
            command.ExecuteNonQuery();
        }
    }

    public void JobFinished(string promptId, bool success, string? outputFile, string? error)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE jobs
                   SET status       = $status,
                       completed_at = $at,
                       started_at   = COALESCE(started_at, $at),
                       output_file  = $file,
                       error        = $error
                 WHERE prompt_id = $id
                """;
            command.Parameters.AddWithValue("$id", promptId);
            command.Parameters.AddWithValue("$status", success ? "completed" : "failed");
            command.Parameters.AddWithValue("$at", Now());
            command.Parameters.AddWithValue("$file", (object?)outputFile ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Recorded when the pool settles the job. Integer satang — the value the pool sent, not a local guess.</summary>
    public void JobSettled(string promptId, long payoutSatang)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET payout_satang = $p WHERE prompt_id = $id";
            command.Parameters.AddWithValue("$id", promptId);
            command.Parameters.AddWithValue("$p", payoutSatang);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Jobs still open past <paramref name="olderThan"/> — what the reconciler settles from ComfyUI's history.</summary>
    public IReadOnlyList<string> UnsettledJobs(TimeSpan olderThan)
    {
        long cutoff = DateTimeOffset.UtcNow.Subtract(olderThan).ToUnixTimeMilliseconds();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT prompt_id FROM jobs
             WHERE status IN ('queued', 'running') AND submitted_at < $cutoff
             ORDER BY submitted_at LIMIT 20
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    public IReadOnlyList<JobRecord> RecentJobs(int limit = 100)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT prompt_id, kind, status, nodes_total, submitted_at, started_at, completed_at, output_file, error, payout_satang
              FROM jobs ORDER BY submitted_at DESC LIMIT $n
            """;
        command.Parameters.AddWithValue("$n", limit);

        var rows = new List<JobRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new JobRecord
            {
                PromptId = reader.GetString(0),
                Kind = reader.GetString(1),
                Status = reader.GetString(2) switch
                {
                    "running" => JobStatus.Running,
                    "completed" => JobStatus.Completed,
                    "failed" => JobStatus.Failed,
                    _ => JobStatus.Queued,
                },
                NodesTotal = reader.GetInt32(3),
                SubmittedAt = FromUnix(reader.GetInt64(4)),
                StartedAt = reader.IsDBNull(5) ? null : FromUnix(reader.GetInt64(5)),
                CompletedAt = reader.IsDBNull(6) ? null : FromUnix(reader.GetInt64(6)),
                OutputFilename = reader.IsDBNull(7) ? null : reader.GetString(7),
                Error = reader.IsDBNull(8) ? null : reader.GetString(8),
                PayoutThb = reader.IsDBNull(9) ? null : reader.GetInt64(9) / 100m,
            });
        }
        return rows;
    }

    /// <summary>
    /// Counts and earnings since a moment — what the dashboard shows.
    /// </summary>
    /// <remarks>
    /// Computed in SQL rather than kept as running totals, so the number on
    /// screen can never drift from the rows behind it. It also survives a
    /// restart, which the in-memory version did not: "jobs done today" used to
    /// go back to zero whenever the owner rebooted mid-afternoon.
    /// </remarks>
    public (int Completed, int Failed, decimal? EarnedThb) Totals(DateTimeOffset since)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'failed'    THEN 1 ELSE 0 END),
                SUM(payout_satang)
              FROM jobs
             WHERE completed_at >= $since
            """;
        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (0, 0, null);

        return (
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2) / 100m);
    }

    /// <summary>
    /// The median wall-clock seconds this machine actually took for a kind of
    /// job, or null when it has not done enough of them to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assessment's best input, and it is free: every job's start
    /// and finish are already recorded. A synthetic benchmark can only predict;
    /// these rows are what the machine did, on the models it really has, with
    /// whatever else its owner runs on it.
    /// </para>
    /// <para>
    /// Median and not mean, because one job that ran while the owner was
    /// playing a game would drag an average far enough to make the node look
    /// unusable. Only completed jobs count — a failure's duration says nothing
    /// about how long the work takes.
    /// </para>
    /// </remarks>
    public double? MedianJobSeconds(string kind, int minSamples = 3, int window = 25)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (completed_at - started_at) / 1000.0 AS secs
              FROM jobs
             WHERE kind = $kind
               AND status = 'completed'
               AND started_at IS NOT NULL
               AND completed_at IS NOT NULL
               AND completed_at > started_at
             ORDER BY completed_at DESC
             LIMIT $window
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$window", window);

        var samples = new List<double>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) samples.Add(reader.GetDouble(0));
        }

        if (samples.Count < minSamples) return null;

        samples.Sort();
        int middle = samples.Count / 2;
        return samples.Count % 2 == 1
            ? samples[middle]
            : (samples[middle - 1] + samples[middle]) / 2;
    }

    // ------------------------------------------------------------- log

    public void AppendLog(DateTimeOffset at, string channel, LogLevel level, string message)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO log (at, channel, level, message) VALUES ($at, $ch, $lv, $msg)";
            command.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$ch", channel);
            command.Parameters.AddWithValue("$lv", (int)level);
            command.Parameters.AddWithValue("$msg", message);
            command.ExecuteNonQuery();
        }
    }

    /// <remarks>
    /// Reading history must never be able to take the node down. The window
    /// builds its Activity Log screen from this while it is still starting up,
    /// so a database that goes bad after it was opened — a bad sector, a host
    /// that lost power mid-write — used to surface here as an unhandled
    /// exception through the view model's constructor, and the owner got no
    /// window at all. Losing the list is a screen with nothing in it; throwing
    /// is a client that will not open.
    /// </remarks>
    public IReadOnlyList<LogEntry> RecentLog(int limit = 500, string? channelFilter = null, bool warningsOnly = false)
    {
        try
        {
            return ReadLog(limit, channelFilter, warningsOnly);
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    private IReadOnlyList<LogEntry> ReadLog(int limit, string? channelFilter, bool warningsOnly)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT at, channel, level, message FROM log
             WHERE 1 = 1
               {(channelFilter is null ? "" : "AND channel IN (SELECT value FROM json_each($channels))")}
               {(warningsOnly ? "AND level = 1" : "")}
             ORDER BY id DESC LIMIT $n
            """;
        command.Parameters.AddWithValue("$n", limit);
        if (channelFilter is not null) command.Parameters.AddWithValue("$channels", channelFilter);

        var rows = new List<LogEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new LogEntry(
                FromUnix(reader.GetInt64(0)),
                reader.GetString(1),
                reader.GetString(3),
                (LogLevel)reader.GetInt32(2)));
        }
        rows.Reverse();   // oldest first, the way a console reads
        return rows;
    }

    // ------------------------------------------------------------- upkeep

    /// <summary>Drops rows past <see cref="Retention"/> and reclaims the pages. Cheap; runs nightly.</summary>
    public void Sweep()
    {
        long cutoff = DateTimeOffset.UtcNow.Subtract(Retention).ToUnixTimeMilliseconds();
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM log  WHERE at < $cutoff;
                DELETE FROM jobs WHERE completed_at IS NOT NULL AND completed_at < $cutoff;
                PRAGMA wal_checkpoint(TRUNCATE);
                """;
            command.Parameters.AddWithValue("$cutoff", cutoff);
            command.ExecuteNonQuery();
        }
    }

    public long SizeBytes()
    {
        try { return new FileInfo(Path).Length; } catch { return 0; }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static DateTimeOffset FromUnix(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();

    public void Dispose()
    {
        _keepAlive.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
