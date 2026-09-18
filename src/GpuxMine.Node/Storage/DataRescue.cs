namespace GpuxMine.Node.Storage;

/// <summary>
/// Moves a node's data out of the old location, which turned out to be inside
/// the install tree.
/// </summary>
/// <remarks>
/// <para>
/// Runs before the database is opened, on every start, and does nothing at all
/// once there is data in the new place. It is cheap enough to leave in
/// permanently and the alternative — telling owners to move a file by hand —
/// is how a fleet ends up half-migrated.
/// </para>
/// <para>
/// It cannot undo the loss on a machine that has already run the 0.1.0
/// installer: by then Velopack has cleaned the directory. What it saves is
/// every node that installed the portable build, ran from a zip, or simply has
/// not updated yet — which on a network this young is nearly all of them.
/// </para>
/// </remarks>
public static class DataRescue
{
    /// <summary>
    /// Plain files that can simply be moved. The database is not one of them.
    /// </summary>
    /// <remarks>
    /// <b>node.db-shm must never be carried across, and that is not a style
    /// preference.</b> It is SQLite's shared-memory index into the WAL,
    /// recreated from scratch whenever the database is opened. Moving it — as
    /// the first version of this did, alongside the database and the WAL, one
    /// file at a time — hands the next process an index that does not describe
    /// the WAL beside it, and SQLite reports the result as
    /// <c>database disk image is malformed</c>. That is exactly what happened
    /// on the machine this was written on: the client updated, moved its data,
    /// and then could not start at all.
    /// </remarks>
    private static readonly string[] PlainFiles = ["agent.json", "update-attempts.txt"];

    /// <summary>
    /// Moves the database by letting SQLite fold the WAL into it first.
    /// </summary>
    /// <remarks>
    /// A SQLite database in WAL mode is three files that only mean anything
    /// together, and moving them one at a time produces a set that does not
    /// agree with itself. Opening it and checkpointing turns it back into a
    /// single self-contained file; after that there is exactly one thing to
    /// move and nothing left to get out of step.
    /// </remarks>
    private static int MoveDatabase(string legacy, string dataDirectory, ILoggerish? log)
    {
        string from = Path.Combine(legacy, "node.db");
        string to = Path.Combine(dataDirectory, "node.db");
        if (!File.Exists(from) || File.Exists(to)) return 0;

        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = from,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                command.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            File.Move(from, to);

            // Whatever is left is now empty or meaningless. Deleting it beats
            // leaving a WAL behind for a future version of this code to find
            // and helpfully carry somewhere.
            foreach (string leftover in new[] { from + "-wal", from + "-shm" })
                try { File.Delete(leftover); } catch { /* not ours to insist on */ }

            return 1;
        }
        catch (Exception ex)
        {
            // Better to start with an empty ledger in the right place than to
            // move a database we could not make consistent.
            log?.Warn($"[warn] could not move the old ledger, starting fresh: {ex.Message}");
            return 0;
        }
    }

    public static void FromLegacyLocation(string dataDirectory, ILoggerish? log = null)
    {
        try
        {
            string legacy = NodeOptions.LegacyDataDirectory();
            if (string.Equals(legacy, dataDirectory, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(legacy)) return;

            // Anything already here wins. A rescue must never overwrite the
            // ledger a node is currently keeping.
            if (File.Exists(Path.Combine(dataDirectory, "node.db"))) return;
            if (!File.Exists(Path.Combine(legacy, "node.db")) && !File.Exists(Path.Combine(legacy, "agent.json"))) return;

            Directory.CreateDirectory(dataDirectory);

            int moved = MoveDatabase(legacy, dataDirectory, log);

            foreach (string name in PlainFiles)
            {
                string from = Path.Combine(legacy, name);
                string to = Path.Combine(dataDirectory, name);
                if (!File.Exists(from) || File.Exists(to)) continue;

                try
                {
                    File.Move(from, to);
                    moved++;
                }
                catch (IOException)
                {
                    // Held by another instance, or by the installer. Copy instead:
                    // a second copy of a settings file is better than none.
                    try { File.Copy(from, to, overwrite: false); moved++; } catch { /* leave it behind */ }
                }
            }

            if (moved > 0)
                log?.Info($"[cfg] moved {moved} file(s) out of the install folder to {dataDirectory}");
        }
        catch (Exception ex)
        {
            // Never block startup over this. A node that starts with an empty
            // ledger still earns; a node that will not start earns nothing.
            log?.Warn($"[warn] could not move the old data folder: {ex.Message}");
        }
    }
}
