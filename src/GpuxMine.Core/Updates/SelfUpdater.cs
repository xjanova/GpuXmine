using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace GpuxMine.Core.Updates;

public enum UpdateOutcome
{
    /// <summary>Not installed through the installer — a dev run or a plain unzip.</summary>
    NotInstalled,
    UpToDate,
    Downloaded,
    Failed,
    /// <summary>Tried repeatedly and the version never moved. Something is holding the install directory.</summary>
    GaveUp,
}

public sealed record UpdateCheck(UpdateOutcome Outcome, string? AvailableVersion, string? Detail);

/// <summary>
/// Binary self-update, pulled from GitHub Releases by Velopack — the same
/// mechanism the BrainX client uses.
/// </summary>
/// <remarks>
/// <para>
/// XMAN Studio is not in this path. It syncs the same GitHub releases so the
/// website can show the version and serve a first-time download, and it holds
/// the licence and device registry; but the bytes that replace this program
/// come from the release feed, verified by Velopack.
/// </para>
/// <para>
/// <b>Three protections against the restart loop BrainX hit on 2026-08-01.</b>
/// That incident was measured precisely: applying an update renames the
/// <c>current</c> directory, and a single handle on that directory — a process
/// whose working directory is inside it is enough — makes the rename fail. The
/// app then relaunched, found the staged update again and retried, every 18–20
/// seconds, for as long as it was running. Worse, <c>Update.exe --waitPid</c>
/// logged "Access is denied" and carried on without waiting at all.
/// </para>
/// <list type="number">
///   <item><see cref="Prepare"/> moves our working directory out of the install
///     tree at startup, so this process is never the holder.</item>
///   <item>The caller must stop the local runtime (and therefore its child
///     processes) before <see cref="ApplyAndRestart"/> — a Python child started
///     from the install tree would hold it just as effectively.</item>
///   <item>A persisted attempt counter: after
///     <see cref="MaxApplyAttempts"/> tries at the same version we stop trying
///     and report it, instead of looping until someone notices.</item>
/// </list>
/// </remarks>
public sealed class SelfUpdater
{
    /// <summary>Velopack's own advice once a rename keeps failing is to reboot. Say so rather than loop.</summary>
    public const int MaxApplyAttempts = 3;

    private readonly string _repoUrl;
    private readonly string _stateFile;
    private readonly Action<string> _log;
    private UpdateManager? _manager;
    private UpdateInfo? _staged;

    public SelfUpdater(string repoUrl, string stateDirectory, Action<string> log)
    {
        _repoUrl = repoUrl;
        _stateFile = Path.Combine(stateDirectory, "update-attempts.txt");
        _log = log;
    }

    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Moves the process out of the install directory, so an update can rename
    /// it later.
    /// </summary>
    /// <remarks>
    /// <b>Call <c>VelopackApp.Build().Run()</c> first, in <c>Main</c> itself.</b>
    /// It used to be called from here, which read better and did not work: the
    /// packer verifies the call is present in the <i>entry assembly</i> and
    /// refuses to build a package when it only finds it in a referenced
    /// library — "Unable to verify VelopackApp is called", on a client whose
    /// hooks would in fact have run correctly. It also belongs in Main on its
    /// own merits: the install, update and uninstall hooks are delivered by
    /// re-running the executable with special arguments, and handling them is
    /// the first thing the program does, before any configuration is read.
    /// </remarks>
    public static void Prepare(string workingDirectory)
    {
        try
        {
            Directory.CreateDirectory(workingDirectory);
            Environment.CurrentDirectory = workingDirectory;
        }
        catch
        {
            // Not fatal on its own — it only raises the chance of the rename
            // conflict this guards against.
        }
    }

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            _manager ??= new UpdateManager(new GithubSource(_repoUrl, accessToken: null, prerelease: false));

            if (!_manager.IsInstalled)
                return new UpdateCheck(UpdateOutcome.NotInstalled, null, "ไม่ได้ติดตั้งผ่านตัวติดตั้ง — ข้ามการอัปเดตอัตโนมัติ");

            UpdateInfo? update = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
            if (update is null)
                return new UpdateCheck(UpdateOutcome.UpToDate, null, null);

            string version = update.TargetFullRelease.Version.ToString();

            if (AttemptsFor(version) >= MaxApplyAttempts)
            {
                return new UpdateCheck(UpdateOutcome.GaveUp, version,
                    $"พยายามติดตั้ง {version} แล้ว {MaxApplyAttempts} ครั้งแต่เวอร์ชันไม่เปลี่ยน " +
                    "— มีบางอย่างถือโฟลเดอร์ติดตั้งไว้ กรุณารีสตาร์ตเครื่องแล้วเปิดใหม่");
            }

            await _manager.DownloadUpdatesAsync(update).WaitAsync(ct);
            _staged = update;

            return new UpdateCheck(UpdateOutcome.Downloaded, version, null);
        }
        catch (Exception ex)
        {
            // An update that cannot be fetched must never take the agent down;
            // the node goes on earning on the version it has.
            _log($"update check failed: {ex.Message}");
            return new UpdateCheck(UpdateOutcome.Failed, null, ex.Message);
        }
    }

    /// <summary>
    /// Replaces this build and restarts.
    /// </summary>
    /// <remarks>
    /// Does not return when it succeeds. The caller must already have stopped
    /// the local runtime and closed the relay connection: whatever is still
    /// running from the install tree is what makes the rename fail.
    /// </remarks>
    /// <param name="restartArgs">
    /// What the new build is started with. This used to be nothing at all, so
    /// the window came back as if the owner had opened it by hand — not
    /// sharing — and every release quietly switched the fleet off.
    /// </param>
    public void ApplyAndRestart(string[]? restartArgs = null)
    {
        if (_manager is null || _staged is null) return;

        // Written before the attempt, not after: if applying kills us mid-way
        // — which is the normal, successful path — a counter written afterwards
        // would never be written at all, and the loop guard would never count.
        RecordAttempt(_staged.TargetFullRelease.Version.ToString());

        _log($"applying update {_staged.TargetFullRelease.Version} and restarting");
        _manager.ApplyUpdatesAndRestart(_staged, restartArgs);
    }

    /// <summary>Clears the counter once we are demonstrably running the new build.</summary>
    public void NoteVersionRunning()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;

            string[] parts = File.ReadAllText(_stateFile).Split(':');
            if (parts.Length == 2 && parts[0] == CurrentVersion)
            {
                File.Delete(_stateFile);
                _log($"update to {CurrentVersion} confirmed");
            }
        }
        catch
        {
            // The counter is a safety net, not state anyone depends on.
        }
    }

    private int AttemptsFor(string version)
    {
        try
        {
            if (!File.Exists(_stateFile)) return 0;

            string[] parts = File.ReadAllText(_stateFile).Split(':');
            return parts.Length == 2 && parts[0] == version && int.TryParse(parts[1], out int count)
                ? count
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void RecordAttempt(string version)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile, $"{version}:{AttemptsFor(version) + 1}");
        }
        catch
        {
            // Losing the counter costs us the loop guard, not correctness.
        }
    }
}
