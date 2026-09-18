using System.Text.Json.Serialization;

namespace GpuxMine.Protocol;

/// <summary>
/// What a frame carries. The tunnel is deliberately dumb: the relay does not
/// understand ComfyUI, it moves whole HTTP request/response pairs between
/// aixman and an agent that dialled out. Adding a ComfyUI endpoint upstream
/// therefore needs no change here.
/// </summary>
public static class FrameKind
{
    /// <summary>agent to relay, first frame on a fresh socket.</summary>
    public const string Hello = "hello";

    /// <summary>relay to agent, accepts the session.</summary>
    public const string HelloAck = "hello-ack";

    /// <summary>relay to agent: run this HTTP request against your local runtime.</summary>
    public const string Request = "req";

    /// <summary>agent to relay: the answer to a <see cref="Request"/>.</summary>
    public const string Response = "res";

    /// <summary>agent to relay: liveness plus telemetry.</summary>
    public const string Heartbeat = "hb";

    /// <summary>either direction: the session is going away, with a reason.</summary>
    public const string Bye = "bye";
}

/// <summary>
/// The JSON part of a frame. The body — a request or response payload — travels
/// as raw bytes after it, never base64: a rendered PNG coming back through
/// <c>/view</c> would otherwise grow by a third on every hop.
/// </summary>
public sealed class TunnelHeader
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>Correlates <see cref="FrameKind.Response"/> with its <see cref="FrameKind.Request"/>.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    // --- hello ---

    [JsonPropertyName("workerId")]
    public string? WorkerId { get; set; }

    [JsonPropertyName("agentVersion")]
    public string? AgentVersion { get; set; }

    /// <summary>Runtime names the agent can serve, e.g. ["comfyui"].</summary>
    [JsonPropertyName("runtimes")]
    public string[]? Runtimes { get; set; }

    // --- req ---

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>Path plus query, as aixman asked for it — e.g. <c>/view?filename=x.png</c>.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    // --- res ---

    [JsonPropertyName("status")]
    public int? Status { get; set; }

    // --- req + res ---

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    // --- hb ---

    [JsonPropertyName("telemetry")]
    public AgentTelemetry? Telemetry { get; set; }

    // --- bye ---

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// What the agent reports about the machine on every heartbeat. Kept small on
/// purpose — the relay stores only the latest snapshot per worker, and this
/// travels every few seconds from every node in the network.
/// </summary>
public sealed class AgentTelemetry
{
    [JsonPropertyName("gpuName")]
    public string? GpuName { get; set; }

    [JsonPropertyName("vramTotalMb")]
    public int VramTotalMb { get; set; }

    [JsonPropertyName("vramUsedMb")]
    public int VramUsedMb { get; set; }

    [JsonPropertyName("gpuLoadPct")]
    public int GpuLoadPct { get; set; }

    [JsonPropertyName("tempC")]
    public int TempC { get; set; }

    [JsonPropertyName("powerW")]
    public int PowerW { get; set; }

    /// <summary>False while the owner is gaming or the schedule says "my time".</summary>
    [JsonPropertyName("accepting")]
    public bool Accepting { get; set; }

    /// <summary>Share of capacity the owner is donating right now, 0-100.</summary>
    [JsonPropertyName("freeSharePct")]
    public int FreeSharePct { get; set; }

    // --- capability assessment -------------------------------------------
    // A node may not be sent work until it has been measured. These four
    // fields are what the back office lists a machine by, and what the pool
    // filters on before a job is ever offered.

    /// <summary>False until the node has passed a capability assessment. No work is dispatched to it while false.</summary>
    [JsonPropertyName("assessed")]
    public bool Assessed { get; set; }

    /// <summary>Measured capability score: 1000 on the reference card, halving as the card halves in speed.</summary>
    [JsonPropertyName("score")]
    public int Score { get; set; }

    /// <summary>platinum · gold · silver · bronze · basic — or unrated before the first assessment.</summary>
    [JsonPropertyName("tier")]
    public string? Tier { get; set; }

    /// <summary>Kinds of work this machine can do at all, e.g. ["image","upscale"].</summary>
    /// <remarks>
    /// "At all" is the important word, and it is why <see cref="Lanes"/> exists
    /// beside it. A machine in the slow lane for image work belongs in this
    /// list — it really does produce the image — but sending it a customer who
    /// is watching a progress bar is a different decision.
    /// </remarks>
    [JsonPropertyName("canRun")]
    public string[]? CanRun { get; set; }

    /// <summary>
    /// How fast each kind in <see cref="CanRun"/> is: <c>full</c> for work
    /// somebody is waiting on, <c>slow</c> for work sitting in a queue.
    /// </summary>
    /// <remarks>
    /// Without this the pool can only see that a node said yes, and the first
    /// thing it does with a machine measured at four minutes an image is hand
    /// it someone who expected two. A node that has not been re-measured since
    /// this field existed sends nothing, and everything it lists is treated as
    /// the fast lane, which is exactly what the pool assumed before.
    /// </remarks>
    [JsonPropertyName("lanes")]
    public Dictionary<string, string>? Lanes { get; set; }

    /// <summary>
    /// Kinds whose lane was given on trust and not yet earned on real jobs.
    /// </summary>
    /// <remarks>
    /// A new node is put in the fast lane for everything its card can hold, and
    /// graded down by its own finished jobs. Saying which of those promises are
    /// still unproven lets the pool start such a machine on something forgiving
    /// rather than on its most impatient customer.
    /// </remarks>
    [JsonPropertyName("provisional")]
    public string[]? Provisional { get; set; }

    /// <summary>The machine's own name, so the back office lists rigs the way their owner talks about them.</summary>
    [JsonPropertyName("host")]
    public string? Host { get; set; }
}
