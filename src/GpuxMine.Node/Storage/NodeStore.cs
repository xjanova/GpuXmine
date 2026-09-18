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

    private readonly string _connectionString;
    private readonly SqliteConnection _keepAlive;
    private readonly Lock _writeGate = new();

    public string Path { get; }

    public NodeStore(string databasePath)
    {
        Path = databasePath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        // One connection held open for the life of the node. Without it the
        // last connection closing would delete the WAL and checkpoint on every
        // single write, which is most of the cost of using SQLite badly.
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        Execute(_keepAlive, """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA foreign_keys = ON;
            """);

        Migrate();
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

    public IReadOnlyList<LogEntry> RecentLog(int limit = 500, string? channelFilter = null, bool warningsOnly = false)
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
