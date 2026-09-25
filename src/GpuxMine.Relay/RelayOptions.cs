namespace GpuxMine.Relay;

/// <summary>
/// Everything the relay reads from configuration, bound from the <c>Relay</c>
/// section (<c>Relay__X</c> in the environment, as the systemd unit sets it).
/// </summary>
/// <remarks>
/// The defaults are what production runs with. Each exists so a limit can be
/// moved without a rebuild on the day a real workload disagrees with it — not
/// as an invitation to tune.
/// </remarks>
public sealed class RelayOptions
{
    /// <summary>Where the worker registry lives. Outlives releases; see RELAY-DEPLOY.md.</summary>
    public string? StorePath { get; set; }

    /// <summary>Enrols, disables, rotates and deletes workers. Falls back to <c>GPUXMINE_ADMIN_KEY</c>.</summary>
    public string? AdminKey { get; set; }

    /// <summary>
    /// Reads <c>GET /admin/workers</c> and nothing else. Falls back to
    /// <c>GPUXMINE_OBSERVER_KEY</c>. What a dispatcher that only needs to know
    /// who is online should hold, instead of the key that can enrol a worker.
    /// </summary>
    public string? ObserverKey { get; set; }

    /// <summary>
    /// Give each newly enrolled worker a tunnel token of its own, separate
    /// from the token its node connects with. Off by default, and only for
    /// deploy order: XMAN Studio builds from before the split push the node's
    /// token to aixman as the tunnel credential, and a split worker refuses
    /// that token on <c>/w/</c> — every machine paired in between would enrol
    /// and never be reachable. Turn it on once XMAN Studio stores and pushes
    /// <c>tunnelToken</c>. While off, <c>/enroll</c> still answers
    /// <c>tunnelToken</c>, equal to <c>token</c>.
    /// </summary>
    public bool IssueTunnelTokens { get; set; }

    /// <summary>Trust X-Forwarded-* from the proxy in front. See the comment in <see cref="RelayHost"/>.</summary>
    public bool TrustProxy { get; set; }

    /// <summary>
    /// How long a tunnel request may go with nothing arriving from the node —
    /// before its answer starts, or between two pieces of it. Not a cap on the
    /// whole exchange: a large file on a slow uplink is busy, not stuck.
    /// </summary>
    public int TunnelTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Largest request body the relay accepts, on any route. Set explicitly on
    /// Kestrel too; before it was Kestrel's silent 30,000,000-byte default.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>Largest reply the relay will pass through for one request. Past it the reply is cut off.</summary>
    public long MaxReplyBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Reply bytes the relay will hold for one agent at a time, waiting for
    /// aixman to read them. One reply may use at most half, so a large download
    /// never starves that node's health probe.
    /// </summary>
    public long SessionBufferBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>
    /// Bytes the relay will hold for everyone together — request bodies on
    /// their way to a node plus replies on their way to aixman. Kept well under
    /// the unit's MemoryHigh so the process is never the thing that gets killed.
    /// </summary>
    public long BufferBudgetBytes { get; set; } = 384L * 1024 * 1024;

    /// <summary>
    /// How much of <see cref="BufferBudgetBytes"/> request bodies may take
    /// between them. The rest is always there for replies, so uploads piling up
    /// behind nodes that are slow to take them can never stop another node's
    /// answers — a readiness probe, a finished render — from getting through.
    /// </summary>
    public long RequestBufferBudgetBytes { get; set; } = 192L * 1024 * 1024;

    /// <summary>
    /// Request bytes the relay will hold for one worker at a time, on their way
    /// to its node. Never less than <see cref="MaxRequestBodyBytes"/>. Before
    /// this, request bodies counted only against the relay-wide budget, and one
    /// node that stopped reading could hold all of it.
    /// </summary>
    public long SessionRequestBufferBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// How long one write to a node — a megabyte of an upload, or a frame's
    /// header — may take before the node is treated as having stopped reading
    /// and its session is closed. A node that keeps sending heartbeats but
    /// never reads would otherwise hold every request sent to it, and the
    /// buffers they sit in, for as long as it liked.
    /// </summary>
    public int SendStallSeconds { get; set; } = 30;

    /// <summary>
    /// How long a node's reply may wait for room in a full buffer (aixman not
    /// reading) before that one reply is abandoned. Short on purpose: while it
    /// waits, that node's other traffic waits behind it.
    /// </summary>
    public int StallSeconds { get; set; } = 10;

    /// <summary>
    /// A session that has sent nothing at all — not a heartbeat, not a byte —
    /// for this long is treated as gone. Catches the laptop that went to sleep,
    /// which TCP alone takes about fifteen minutes to notice.
    /// </summary>
    public int StaleAfterSeconds { get; set; } = 90;

    /// <summary>
    /// WebSocket ping-pong timeout, 0 for off (the default). Off because a v0.1
    /// agent sends a whole reply as one frame and cannot answer a ping until
    /// it is done — a large file on a slow uplink would be cut off as "dead".
    /// <see cref="StaleAfterSeconds"/> counts bytes instead, which a busy node
    /// is always sending.
    /// </summary>
    public int KeepAliveTimeoutSeconds { get; set; }

    /// <summary>Advertise streamed replies to agents. Off makes the relay look like v0.1 to them, for a rollback drill.</summary>
    public bool StreamReplies { get; set; } = true;

    /// <summary>Connection attempts per minute from one address to <c>/agent</c>.</summary>
    public int AgentConnectsPerMinutePerIp { get; set; } = 60;

    /// <summary>
    /// Connection attempts per minute for one worker id. Two PCs that share an
    /// identity evict each other on every connect; this is what stops that
    /// from becoming a reconnect storm.
    /// </summary>
    public int AgentConnectsPerMinutePerWorker { get; set; } = 12;

    /// <summary>Tunnel requests in flight at once for one worker.</summary>
    public int TunnelConcurrencyPerWorker { get; set; } = 16;

    /// <summary>Tunnel requests per minute for one worker.</summary>
    public int TunnelRequestsPerMinutePerWorker { get; set; } = 600;

    /// <summary>Requests per minute from one address to <c>/enroll</c> and <c>/admin/*</c>, and to <c>/w/</c> for an id the relay does not know.</summary>
    public int AdminRequestsPerMinutePerIp { get; set; } = 1200;
}
