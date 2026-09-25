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
    /// <summary>The window used when the owner has not chosen one.</summary>
    /// <remarks>
    /// Long enough for any dispute over a payout, short enough that the file
    /// stays small on a machine nobody is looking after.
    /// </remarks>
    public const int DefaultRetentionDays = 120;

    /// <summary>
    /// How many days of finished work to keep, or 0 to keep all of it.
    /// </summary>
    /// <remarks>
    /// This used to be a constant, which meant an owner who wanted a month of
    /// history and a small file had no way to ask for one, and an owner who
    /// wanted to keep everything had no way to stop the sweep taking it. It is
    /// their machine and their record of their own work.
    /// </remarks>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

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

            // Added after the table shipped, so added here rather than in the
            // CREATE: a ledger from an earlier build already has the table and
            // CREATE IF NOT EXISTS would leave it without them.
            //   purged_at    unix ms when the job's files and history were
            //                taken off this machine; NULL until then
            //   input_files  JSON [{filename, subfolder, type}] aixman uploaded
            //                for the job, so they can be purged with it
            EnsureColumn(connection, "jobs", "purged_at", "INTEGER");
            EnsureColumn(connection, "jobs", "input_files", "TEXT");

            // What the pool settled the job at, from XMAN Studio (contract C6):
            //   payout_status   pending | review | cleared | paid | void — where
            //                   payout_satang is on its way to the wallet; NULL
            //                   until the pool has settled the job at all
            //   donated_satang  what a free-share job would have paid
            //   pool_job_id     the pool's own id for the job, for a support ticket
            //   settled_at      unix ms of the last change the website reported
            EnsureColumn(connection, "jobs", "payout_status", "TEXT");
            EnsureColumn(connection, "jobs", "donated_satang", "INTEGER");
            EnsureColumn(connection, "jobs", "pool_job_id", "TEXT");
            EnsureColumn(connection, "jobs", "settled_at", "INTEGER");
        }
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string type)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c";
            probe.Parameters.AddWithValue("$c", column);
            if (Convert.ToInt32(probe.ExecuteScalar() ?? 0) > 0) return;
        }
        Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
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

    /// <summary>The settlement states XMAN Studio reports, in the order a job moves through them.</summary>
    public static readonly IReadOnlySet<string> PayoutStatuses =
        new HashSet<string>(["pending", "review", "cleared", "paid", "void"], StringComparer.Ordinal);

    /// <summary>
    /// Records what the pool settled a job at. Integer satang — the value the
    /// pool sent, never a local guess.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called on every status poll with the same fifty jobs, so it writes only
    /// what changed and says whether it did: the caller tells the owner about a
    /// job being settled or reaching the wallet once, not every three minutes.
    /// </para>
    /// <para>
    /// A prompt id this ledger does not hold — swept past retention, or from
    /// before a reinstall — is ignored. The website is the authority for the
    /// money; this table only mirrors it for this machine's own jobs.
    /// </para>
    /// </remarks>
    /// <param name="status">pending · review · cleared · paid · void. Anything else is stored as null (unknown).</param>
    /// <returns>True when the row existed and something about its settlement changed.</returns>
    public bool JobSettled(string promptId, long payoutSatang, string? status = null, long donatedSatang = 0, string? poolJobId = null)
    {
        if (string.IsNullOrEmpty(promptId)) return false;

        string? known = status is not null && PayoutStatuses.Contains(status.Trim().ToLowerInvariant())
            ? status.Trim().ToLowerInvariant()
            : null;
        string? jobId = string.IsNullOrWhiteSpace(poolJobId) ? null : poolJobId.Trim()[..Math.Min(poolJobId.Trim().Length, 96)];

        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            // IS NOT compares NULLs as values, so "no change" really is no write.
            command.CommandText = """
                UPDATE jobs
                   SET payout_satang  = $p,
                       payout_status  = $s,
                       donated_satang = $d,
                       pool_job_id    = COALESCE($j, pool_job_id),
                       settled_at     = $at
                 WHERE prompt_id = $id
                   AND (payout_satang  IS NOT $p
                     OR payout_status  IS NOT $s
                     OR donated_satang IS NOT $d
                     OR ($j IS NOT NULL AND pool_job_id IS NOT $j))
                """;
            command.Parameters.AddWithValue("$id", promptId);
            command.Parameters.AddWithValue("$p", payoutSatang);
            command.Parameters.AddWithValue("$s", (object?)known ?? DBNull.Value);
            command.Parameters.AddWithValue("$d", donatedSatang);
            command.Parameters.AddWithValue("$j", (object?)jobId ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", Now());
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Jobs still open past <paramref name="olderThan"/> — what the reconciler settles from ComfyUI's history.</summary>
    public IReadOnlyList<string> UnsettledJobs(TimeSpan olderThan) =>
        UnsettledPage(olderThan, limit: 20).Select(j => j.PromptId).ToArray();

    /// <summary>One open job, and when it was submitted — the key the next page starts after.</summary>
    public sealed record OpenJob(string PromptId, DateTimeOffset SubmittedAt, long SubmittedAtMs);

    /// <summary>
    /// A page of jobs still open past <paramref name="olderThan"/>, oldest
    /// first, starting after <paramref name="after"/>.
    /// </summary>
    /// <remarks>
    /// Paged rather than "the twenty oldest": a row ComfyUI no longer knows
    /// about stayed at the front of that list forever, and every job newer than
    /// the twentieth such row was never reconciled at all.
    /// </remarks>
    public IReadOnlyList<OpenJob> UnsettledPage(TimeSpan olderThan, int limit = 50, OpenJob? after = null)
    {
        long cutoff = DateTimeOffset.UtcNow.Subtract(olderThan).ToUnixTimeMilliseconds();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT prompt_id, submitted_at FROM jobs
             WHERE status IN ('queued', 'running') AND submitted_at < $cutoff
               AND (submitted_at > $afterAt OR (submitted_at = $afterAt AND prompt_id > $afterId))
             ORDER BY submitted_at, prompt_id LIMIT $n
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$afterAt", after?.SubmittedAtMs ?? long.MinValue);
        command.Parameters.AddWithValue("$afterId", after?.PromptId ?? "");
        command.Parameters.AddWithValue("$n", limit);

        var rows = new List<OpenJob>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long at = reader.GetInt64(1);
            rows.Add(new OpenJob(reader.GetString(0), FromUnix(at), at));
        }
        return rows;
    }

    /// <summary>Whether this prompt came down the tunnel — the only prompts a purge may touch.</summary>
    public bool HasJob(string promptId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM jobs WHERE prompt_id = $id";
        command.Parameters.AddWithValue("$id", promptId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Which of <paramref name="promptIds"/> came down the tunnel — the
    /// ledger's half of <see cref="HasJob"/>, for a whole queue at once.
    /// </summary>
    /// <remarks>
    /// One query per few hundred ids rather than one per id: it is asked on
    /// every readiness probe, and the owner's own batch can be long.
    /// </remarks>
    public IReadOnlySet<string> JobsAmong(IReadOnlyCollection<string> promptIds)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (promptIds.Count == 0) return found;

        using var connection = Open();
        foreach (string[] chunk in promptIds.Chunk(500))
        {
            using var command = connection.CreateCommand();
            var names = new string[chunk.Length];
            for (int i = 0; i < chunk.Length; i++)
            {
                names[i] = "$p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue(names[i], chunk[i]);
            }
            command.CommandText = $"SELECT prompt_id FROM jobs WHERE prompt_id IN ({string.Join(',', names)})";

            using var reader = command.ExecuteReader();
            while (reader.Read()) found.Add(reader.GetString(0));
        }
        return found;
    }

    /// <summary>When a job was recorded as submitted, or null when the ledger does not hold it.</summary>
    public DateTimeOffset? JobSubmittedAt(string promptId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT submitted_at FROM jobs WHERE prompt_id = $id";
        command.Parameters.AddWithValue("$id", promptId);
        return command.ExecuteScalar() is long at ? FromUnix(at) : null;
    }

    /// <summary>Jobs submitted since <paramref name="since"/> whose files have not been purged, newest first.</summary>
    public IReadOnlyList<string> UnpurgedJobs(DateTimeOffset since, int limit = 10)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT prompt_id FROM jobs
             WHERE purged_at IS NULL AND submitted_at >= $since
             ORDER BY submitted_at DESC LIMIT $n
            """;
        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$n", limit);

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>The job is on record and has not been finished — queued or running.</summary>
    public bool JobIsOpen(string promptId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM jobs WHERE prompt_id = $id AND status IN ('queued', 'running')";
        command.Parameters.AddWithValue("$id", promptId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Records which uploaded files a job reads, so they can be purged with it after a restart.</summary>
    public void JobInputs(string promptId, IReadOnlyList<ComfyFile> inputs)
    {
        if (inputs.Count == 0) return;
        string json = System.Text.Json.JsonSerializer.Serialize(
            inputs.Select(f => new { filename = f.Filename, subfolder = f.Subfolder, type = f.Type }));

        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET input_files = $files WHERE prompt_id = $id";
            command.Parameters.AddWithValue("$id", promptId);
            command.Parameters.AddWithValue("$files", json);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The uploaded files a job reads, as <see cref="JobInputs"/> recorded them.</summary>
    public IReadOnlyList<ComfyFile> InputsOf(string promptId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT input_files FROM jobs WHERE prompt_id = $id";
        command.Parameters.AddWithValue("$id", promptId);
        if (command.ExecuteScalar() is not string json || json.Length == 0) return [];

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var files = new List<ComfyFile>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string? name = item.TryGetProperty("filename", out var f) ? f.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;
                files.Add(new ComfyFile(
                    name,
                    item.TryGetProperty("subfolder", out var s) ? s.GetString() ?? "" : "",
                    item.TryGetProperty("type", out var t) ? t.GetString() ?? "input" : "input"));
            }
            return files;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Records that a job's files and history have been taken off this machine.</summary>
    public void JobPurged(string promptId)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET purged_at = $at WHERE prompt_id = $id AND purged_at IS NULL";
            command.Parameters.AddWithValue("$id", promptId);
            command.Parameters.AddWithValue("$at", Now());
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Finished jobs nobody has purged, that ended between
    /// <paramref name="since"/> and <paramref name="before"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="since"/> is the moment this build first ran: jobs from
    /// before the node knew how to purge are left where the owner has always
    /// seen them, rather than deleted in one sweep on the day of the update.
    /// </remarks>
    public IReadOnlyList<string> JobsToPurge(DateTimeOffset before, DateTimeOffset since, int limit = 50)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT prompt_id FROM jobs
             WHERE purged_at IS NULL
               AND completed_at IS NOT NULL
               AND completed_at < $before
               AND completed_at >= $since
             ORDER BY completed_at LIMIT $n
            """;
        command.Parameters.AddWithValue("$before", before.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$n", limit);

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>The one place the jobs columns are turned into a record.</summary>
    /// <remarks>
    /// Two queries select the same twelve columns in the same order, and a
    /// hand-copied second reader is how the two drift apart by one index.
    /// </remarks>
    private static JobRecord ReadJob(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
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
        PayoutStatus = reader.IsDBNull(10) ? null : reader.GetString(10),
        DonatedThb = reader.IsDBNull(11) ? null : reader.GetInt64(11) / 100m,
    };

    public IReadOnlyList<JobRecord> RecentJobs(int limit = 100)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT prompt_id, kind, status, nodes_total, submitted_at, started_at, completed_at, output_file, error, payout_satang,
                   payout_status, donated_satang
              FROM jobs ORDER BY submitted_at DESC LIMIT $n
            """;
        command.Parameters.AddWithValue("$n", limit);

        var rows = new List<JobRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(ReadJob(reader));
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
        LedgerTotals t = TotalsFor(since);
        return (t.Completed, t.Failed, t.EarnedSatang is { } satang ? satang / 100m : null);
    }

    /// <summary>
    /// Counts and settled money since a moment, including how much of the
    /// finished work the pool has not settled yet.
    /// </summary>
    /// <remarks>
    /// A voided job is left out of the money: it was settled and then taken
    /// back, and counting it would show the owner a figure the wallet will
    /// never see. A job the pool has not settled is left out too, and counted
    /// separately — "not settled yet" is not "earned nothing".
    /// </remarks>
    public LedgerTotals TotalsFor(DateTimeOffset since)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'failed'    THEN 1 ELSE 0 END),
                SUM({Earned}),
                SUM(CASE WHEN status = 'completed' AND payout_satang IS NULL THEN 1 ELSE 0 END),
                SUM(CASE WHEN {IsSettled} THEN 1 ELSE 0 END),
                COALESCE(SUM(CASE WHEN {IsSettled} THEN donated_satang END), 0)
              FROM jobs
             WHERE completed_at >= $since
            """;
        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new LedgerTotals(0, 0, null, 0, 0, 0);

        return new LedgerTotals(
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            reader.GetInt64(5));
    }

    /// <summary>A settled job's pay, or NULL — for SUM(), which then says "nothing settled" as NULL rather than 0.</summary>
    private const string Earned = "CASE WHEN payout_status IS NOT 'void' THEN payout_satang END";

    /// <summary>The pool settled it and did not take it back.</summary>
    private const string IsSettled = "payout_satang IS NOT NULL AND payout_status IS NOT 'void'";

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
    /// <param name="sinceUnixMs">
    /// Ignore anything finished before this instant. The owner's way back to a
    /// maximum assessment: the rows stay — the History screen still shows every
    /// job — but grading starts again from the work done after it. Without this
    /// a machine measured on a bad day carried those seconds around in its
    /// median, and the only way out was to out-run them.
    /// </param>
    public double? MedianJobSeconds(string kind, int minSamples = 3, int window = 25, long? sinceUnixMs = null)
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
               AND completed_at >= $since
             ORDER BY completed_at DESC
             LIMIT $window
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$window", window);
        command.Parameters.AddWithValue("$since", sinceUnixMs ?? 0L);

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

    /// <summary>
    /// How many completed jobs of a kind currently count toward grading, so the
    /// Benchmark screen can say "2 of 3" rather than leaving the owner to guess
    /// when the provisional lane turns into a measured one.
    /// </summary>
    public int GradedJobCount(string kind, long? sinceUnixMs = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
              FROM jobs
             WHERE kind = $kind
               AND status = 'completed'
               AND started_at IS NOT NULL
               AND completed_at IS NOT NULL
               AND completed_at > started_at
               AND completed_at >= $since
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$since", sinceUnixMs ?? 0L);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
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

    /// <summary>
    /// Drops finished work older than <see cref="RetentionDays"/> and reclaims
    /// the pages. Cheap; runs nightly and whenever the owner asks.
    /// </summary>
    /// <returns>How many job rows were removed.</returns>
    public int Sweep()
    {
        // 0 means keep everything. Checkpoint anyway: that is what keeps the
        // write-ahead log from growing without bound, and it is the half of
        // this method that has nothing to do with retention.
        if (RetentionDays <= 0)
        {
            Checkpoint();
            return 0;
        }

        long cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays).ToUnixTimeMilliseconds();
        int removed;
        lock (_writeGate)
        {
            using var counter = Open();
            using var count = counter.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM jobs WHERE completed_at IS NOT NULL AND completed_at < $cutoff";
            count.Parameters.AddWithValue("$cutoff", cutoff);
            removed = Convert.ToInt32(count.ExecuteScalar() ?? 0);
        }

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

        return removed;
    }

    /// <summary>Folds the write-ahead log back into the database file.</summary>
    private void Checkpoint()
    {
        lock (_writeGate)
        {
            using var connection = Open();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
    }

    // ------------------------------------------------------------- history

    /// <summary>
    /// Finished work, newest first, for the owner's own record of what their
    /// machine did.
    /// </summary>
    /// <remarks>
    /// Read from the database rather than from the in-memory queue: the Live
    /// Queue screen shows this session, and a machine that has been earning for
    /// a month has nothing to show there after a restart.
    /// </remarks>
    public IReadOnlyList<JobRecord> FinishedJobs(int days, int limit = 500)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT prompt_id, kind, status, nodes_total, submitted_at, started_at, completed_at, output_file, error, payout_satang,
                   payout_status, donated_satang
              FROM jobs
             WHERE completed_at IS NOT NULL
               AND ($since = 0 OR completed_at >= $since)
             ORDER BY completed_at DESC
             LIMIT $n
            """;
        command.Parameters.AddWithValue("$since", SinceMillis(days));
        command.Parameters.AddWithValue("$n", limit);

        var rows = new List<JobRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(ReadJob(reader));
        return rows;
    }

    /// <summary>Totals over the same window the history is showing.</summary>
    public JobTotals TotalsSince(int days)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                COUNT(*),
                SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END),
                COALESCE(SUM({Earned}), 0),
                COALESCE(SUM(CASE WHEN started_at IS NOT NULL THEN completed_at - started_at ELSE 0 END), 0),
                SUM(CASE WHEN {IsSettled} THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'completed' AND payout_satang IS NULL THEN 1 ELSE 0 END),
                COALESCE(SUM(CASE WHEN {IsSettled} THEN donated_satang END), 0)
              FROM jobs
             WHERE completed_at IS NOT NULL
               AND ($since = 0 OR completed_at >= $since)
            """;
        command.Parameters.AddWithValue("$since", SinceMillis(days));

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new JobTotals();
        return new JobTotals
        {
            Jobs = reader.GetInt32(0),
            Completed = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            Failed = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            PayoutSatang = reader.GetInt64(3),
            BusyTime = TimeSpan.FromMilliseconds(reader.GetInt64(4)),
            Settled = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            Unsettled = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            DonatedSatang = reader.GetInt64(7),
        };
    }

    /// <summary>The oldest finished job still on disk, so the screen can say what "all" covers.</summary>
    public DateTimeOffset? OldestFinishedJob()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(completed_at) FROM jobs WHERE completed_at IS NOT NULL";
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : FromUnix(Convert.ToInt64(value));
    }

    /// <summary>0 for "everything", otherwise the cut-off in unix milliseconds.</summary>
    private static long SinceMillis(int days) =>
        days <= 0 ? 0 : DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();

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
