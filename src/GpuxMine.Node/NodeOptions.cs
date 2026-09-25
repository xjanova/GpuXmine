namespace GpuxMine.Node;

public sealed record NodeOptions
{
    /// <summary>
    /// The relay this node dials out to. Overridden by the identity file the
    /// moment the machine is registered.
    /// </summary>
    /// <remarks>
    /// The default used to be <c>ws://localhost:5080/agent</c>, which is a
    /// developer's own machine and not a thing any owner has. Anything that
    /// left the identity file without a relay — a pairing response missing
    /// <c>relay_url</c>, a hand-edited file, a fresh install read before
    /// registration — showed the owner a client pointed at localhost that
    /// could never connect, and said nothing about why.
    /// A wrong-but-real default fails loudly and in one place; a localhost
    /// default fails silently on every machine that is not ours.
    /// </remarks>
    public string RelayUrl { get; init; } = "wss://relay.xman4289.com:8443/agent";

    public string WorkerId { get; init; } = "";

    /// <summary>
    /// The bearer token minted at enrolment. Read from config for M1; from the
    /// Windows credential store (DPAPI) once the real enrolment flow lands, so
    /// that it never sits on disk in the clear.
    /// </summary>
    public string Token { get; init; } = "";

    /// <summary>Where the local inference server listens.</summary>
    public string ComfyUrl { get; init; } = "http://127.0.0.1:8188";

    /// <summary>
    /// How long one request to the local ComfyUI may take before the node
    /// gives up on it and answers the tunnel itself (1–3600 seconds).
    /// </summary>
    /// <remarks>
    /// Kept under the relay's own wait (180 seconds by default), so a ComfyUI
    /// that takes a request and never answers is reported by the node, with a
    /// stage aixman understands, before the relay gives up and answers a bare
    /// 504 that counts against the machine.
    /// </remarks>
    public int ComfyTimeoutSeconds { get; init; } = 150;

    /// <summary>
    /// The folder ComfyUI runs from (the one holding <c>main.py</c>, or its
    /// <c>--base-directory</c>). Only needed when the node cannot work it out
    /// from ComfyUI itself — see <see cref="ComfyRuntime.FoldersAsync"/>.
    /// </summary>
    /// <remarks>
    /// Used for one thing: deleting a customer's job files once aixman has
    /// them. A node that cannot find the folder still clears the prompt from
    /// ComfyUI's history, and says in the log which files it had to leave.
    /// </remarks>
    public string? ComfyBaseDirectory { get; init; }

    /// <summary>Overrides <c>{ComfyBaseDirectory}/output</c>, as ComfyUI's own <c>--output-directory</c> does.</summary>
    public string? ComfyOutputDirectory { get; init; }

    /// <summary>Overrides <c>{ComfyBaseDirectory}/input</c>, as ComfyUI's own <c>--input-directory</c> does.</summary>
    public string? ComfyInputDirectory { get; init; }

    /// <summary>
    /// Hours after a customer's job ends before the node purges it on its own,
    /// if aixman has not already. 0 leaves it entirely to aixman.
    /// </summary>
    /// <remarks>
    /// The fallback for an aixman that predates <c>/aixman/purge</c>, or one
    /// that could not reach the node at the moment it tried. aixman gives up
    /// on a job long before this, so nothing it could still want is deleted.
    /// </remarks>
    public int PurgeAfterHours { get; init; } = 6;

    /// <summary>Serve a stand-in ComfyUI in-process, to exercise the tunnel without a GPU.</summary>
    public bool Mock { get; init; }

    public int HeartbeatSeconds { get; init; } = 10;

    // --- XMAN Studio (บัญชี ไลเซนส์ และทะเบียนเครื่อง) ---

    public string XmanStudioUrl { get; init; } = "https://xman4289.com";

    /// <summary>Pro Miner license. Empty = free tier, which still earns.</summary>
    public string? LicenseKey { get; init; }

    // --- อัปเดตตัวเอง ---

    /// <summary>GitHub repo ที่ CI ออก release — Velopack ดึงไบนารีจากที่นี่ตรง ๆ</summary>
    public string UpdateRepo { get; init; } = "https://github.com/xjanova/GpuXmine";

    public bool AutoUpdate { get; init; } = true;

    public int UpdateCheckHours { get; init; } = 6;

    /// <summary>
    /// ที่เก็บ token, ตัวนับการอัปเดต, ledger และ log — ต้องอยู่นอกโฟลเดอร์ติดตั้งเสมอ
    /// </summary>
    public string DataDirectory { get; init; } = DefaultDataDirectory();

    /// <summary>
    /// Roaming AppData, and deliberately not Local.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be <c>%LocalAppData%\GpuxMine</c>, which is the exact
    /// directory Velopack installs into. Measured, not theorised: installing
    /// the real 0.1.0 package logged <i>"Renaming existing directory to
    /// GpuxMine.idNriBMGdSl27jIW to allow rollback"</i> and then
    /// <i>"Preparing and cleaning installation directory"</i> — and the node's
    /// database, with its settings, its activity log and every job it had ever
    /// done, was gone.
    /// </para>
    /// <para>
    /// It was also the thing that defeated the guard it was supposed to help:
    /// <see cref="GpuxMine.Core.Updates.SelfUpdater.Prepare"/> moves the
    /// process out of the install tree so an update can rename
    /// <c>current</c>, and it was moving it to another folder inside that same
    /// tree.
    /// </para>
    /// </remarks>
    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

#if DEBUG
    /// <summary>
    /// A development build keeps its own folder, away from the installed node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// They used to share one, and sharing it did real damage. Two processes
    /// held the same SQLite file and the owner's ledger was destroyed three
    /// times in a day. The single-instance lock is per data directory, so a
    /// development build left running also made the owner's shortcut front
    /// <i>that</i> window instead of starting their own — measured, not
    /// theorised: launching the installed build while a debug build was up saw
    /// the installed process exit immediately.
    /// </para>
    /// <para>
    /// A build from a development machine has no business writing to the
    /// identity, the ledger or the settings of a node that is actually earning.
    /// Pass <c>--DataDirectory</c> to point a debug build at the real folder
    /// when that is genuinely what is wanted.
    /// </para>
    /// </remarks>
    private const string FolderName = "GPUxMINE-dev";
#else
    private const string FolderName = "GPUxMINE";
#endif

    /// <summary>Where the data used to live. Read once, to rescue it, then never again.</summary>
    public static string LegacyDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpuxMine");

    /// <summary>
    /// Set when the identity had to be read straight from disk because the
    /// configuration came back without one. Null on every healthy start.
    /// </summary>
    /// <remarks>
    /// Reported in the log rather than kept quiet: this is the evidence that
    /// the unexplained empty-identity start happened again, and which file
    /// saved it.
    /// </remarks>
    public string? IdentityRescuedFrom { get; init; }

    public bool Validate(out string error)
    {
        if (string.IsNullOrWhiteSpace(WorkerId)) { error = "WorkerId is required"; return false; }
        if (string.IsNullOrWhiteSpace(Token)) { error = "Token is required"; return false; }
        if (!Uri.TryCreate(RelayUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
        {
            error = $"RelayUrl must be a ws:// or wss:// URL, got '{RelayUrl}'";
            return false;
        }
        if (!Uri.TryCreate(ComfyUrl, UriKind.Absolute, out var comfy) || comfy.Scheme is not ("http" or "https"))
        {
            // Caught here rather than as an unhandled exception from `new Uri`
            // three seconds into startup, with no hint of which setting was wrong.
            error = $"ComfyUrl must be an http:// or https:// URL, got '{ComfyUrl}'";
            return false;
        }
        if (!Uri.TryCreate(XmanStudioUrl, UriKind.Absolute, out var studio) || studio.Scheme is not ("http" or "https"))
        {
            error = $"XmanStudioUrl must be an http:// or https:// URL, got '{XmanStudioUrl}'";
            return false;
        }
        error = "";
        return true;
    }
}
