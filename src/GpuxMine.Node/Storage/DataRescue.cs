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
    /// <summary>The files worth carrying across. The WAL pair matters: without it, recent writes are lost.</summary>
    private static readonly string[] Files =
        ["node.db", "node.db-wal", "node.db-shm", "agent.json", "update-attempts.txt"];

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

            int moved = 0;
            foreach (string name in Files)
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
                    // a second copy of the ledger is a far better outcome than none.
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
