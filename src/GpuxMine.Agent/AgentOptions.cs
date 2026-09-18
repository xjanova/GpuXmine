namespace GpuxMine.Agent;

public sealed class AgentOptions
{
    /// <summary>e.g. wss://relay.gpuxmine.dev/agent</summary>
    public string RelayUrl { get; init; } = "ws://localhost:5080/agent";

    public string WorkerId { get; init; } = "";

    /// <summary>
    /// The bearer token minted at enrolment. Read from config for M1; from the
    /// Windows credential store (DPAPI) once the real enrolment flow lands, so
    /// that it never sits on disk in the clear.
    /// </summary>
    public string Token { get; init; } = "";

    /// <summary>Where the local inference server listens.</summary>
    public string ComfyUrl { get; init; } = "http://127.0.0.1:8188";

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
    /// ที่เก็บ token, ตัวนับการอัปเดต และ log — ต้องอยู่นอกโฟลเดอร์ติดตั้งเสมอ
    /// ไม่งั้นมันจะกลายเป็นตัวที่ถือ <c>current</c> ไว้จนอัปเดตไม่ได้
    /// </summary>
    public string DataDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpuxMine");

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
