using System.Security.Cryptography;
using System.Text;

namespace GpuxMine.Node;

/// <summary>
/// One node, one process, per data directory.
/// </summary>
/// <remarks>
/// <para>
/// Nothing used to stop a second copy of the node starting, and two copies
/// share everything that matters: the same identity file, the same relay
/// credentials, and the same SQLite ledger. The development machine lost that
/// ledger three times in one day to exactly that — two processes with the file
/// open, one of them killed, the write-ahead log left inconsistent. It also
/// made "open the program" a coin toss, because whichever window came up was
/// whichever copy the shell happened to activate.
/// </para>
/// <para>
/// The lock is per data directory rather than per machine. Two nodes
/// deliberately run side by side with <c>--DataDirectory</c>, each with its own
/// ledger and its own identity; it is sharing one directory that is never
/// intentional. The window and the headless agent take the same lock, because
/// running both against one directory is the pairing that caused the damage.
/// </para>
/// </remarks>
public sealed class NodeInstanceLock : IDisposable
{
    private Mutex? _mutex;

    private NodeInstanceLock(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Claims the directory, or returns null when another process already has it.
    /// </summary>
    /// <param name="wait">
    /// How long to wait for the holder to let go. Zero for an ordinary launch:
    /// a second copy should front the first, not queue behind it. A relaunch
    /// passes <see cref="LaunchFlags.RestartWait"/>, because the process that
    /// started it may still be closing — the new copy used to find the lock
    /// taken, decide it was a duplicate and exit, and the owner was left with
    /// no program at all after a successful pairing or update.
    /// </param>
    public static NodeInstanceLock? TryAcquire(string dataDirectory, TimeSpan wait = default)
    {
        var mutex = new Mutex(initiallyOwned: true, NameFor(dataDirectory), out bool sole);
        if (sole) return new NodeInstanceLock(mutex);

        if (wait > TimeSpan.Zero)
        {
            try
            {
                if (mutex.WaitOne(wait)) return new NodeInstanceLock(mutex);
            }
            catch (AbandonedMutexException)
            {
                // The holder exited without letting go — killed, or crashed on
                // the way out. Windows hands the mutex to us regardless.
                return new NodeInstanceLock(mutex);
            }
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// A name that matches for two copies sharing a directory and differs for
    /// two that are not.
    /// </summary>
    /// <remarks>
    /// The path is hashed rather than used directly: a mutex name cannot
    /// contain a backslash, and Windows caps it at 260 characters. <c>Local\</c>
    /// scope, because the ledger belongs to the signed-in user and two people
    /// logged into the same machine have their own.
    /// </remarks>
    private static string NameFor(string dataDirectory)
    {
        string key = Path.GetFullPath(dataDirectory).TrimEnd('\\').ToLowerInvariant();
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return @"Local\GPUxMINE-node-" + digest[..16];
    }

    public void Dispose()
    {
        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* never owned it */ }
        _mutex.Dispose();
        _mutex = null;
    }
}
