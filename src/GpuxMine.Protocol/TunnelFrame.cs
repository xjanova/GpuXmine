using System.Text.Json;
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

    /// <summary>agent to relay: the answer to a <see cref="Request"/>, whole, in one frame.</summary>
    /// <remarks>
    /// The only reply shape a v0.1.x agent knows, and still what a current
    /// agent sends to a relay that has not advertised
    /// <see cref="TunnelCaps.ResponseStreaming"/>. A relay has to accept it for
    /// as long as agents that old are in the field.
    /// </remarks>
    public const string Response = "res";

    /// <summary>
    /// agent to relay: the status and headers of a reply whose body follows as
    /// <see cref="ResponseChunk"/> frames and ends with <see cref="ResponseEnd"/>.
    /// </summary>
    /// <remarks>
    /// Sent only to a relay that advertised <see cref="TunnelCaps.ResponseStreaming"/>
    /// in its <see cref="HelloAck"/>. Splitting the reply is what lets a relay
    /// hand a finished video to aixman as it arrives instead of holding all of
    /// it in memory first, and what lets its timeout measure silence rather
    /// than size: a slow uplink moving a large file is busy, not stuck.
    /// </remarks>
    public const string ResponseHead = "res-head";

    /// <summary>agent to relay: the next piece of a streamed reply, at most <see cref="WireCodec.MaxChunkBytes"/> of body.</summary>
    public const string ResponseChunk = "res-chunk";

    /// <summary>
    /// agent to relay: the streamed reply is complete — or, with
    /// <see cref="TunnelHeader.Aborted"/> set, will never be, and what was sent
    /// so far must not be passed off as the whole answer.
    /// </summary>
    public const string ResponseEnd = "res-end";

    /// <summary>
    /// relay to agent: nobody is waiting for this request's answer any more
    /// (aixman hung up, or the relay gave up on it). Stop working on it and
    /// send nothing further for its id.
    /// </summary>
    /// <remarks>An agent that does not know this kind ignores it, which is safe: the relay discards what it sends anyway.</remarks>
    public const string Cancel = "cancel";

    /// <summary>agent to relay: liveness plus telemetry.</summary>
    public const string Heartbeat = "hb";

    /// <summary>either direction: the session is going away, with a reason.</summary>
    public const string Bye = "bye";
}

/// <summary>
/// Optional abilities a peer announces. Absent means "behave like v0.1".
/// </summary>
/// <remarks>
/// Negotiated rather than assumed so that either side can be upgraded first: a
/// new agent talking to the relay already in production, and a v0.1 agent
/// talking to a new relay, both keep working. The relay lists what it
/// understands in <see cref="FrameKind.HelloAck"/>; the agent uses a feature
/// only when it is on that list.
/// </remarks>
public static class TunnelCaps
{
    /// <summary>The relay accepts <see cref="FrameKind.ResponseHead"/> / <see cref="FrameKind.ResponseChunk"/> / <see cref="FrameKind.ResponseEnd"/>.</summary>
    public const string ResponseStreaming = "res-stream";
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

    // --- hello-ack ---

    /// <summary>What the relay understands beyond v0.1 — see <see cref="TunnelCaps"/>.</summary>
    [JsonPropertyName("caps")]
    public string[]? Caps { get; set; }

    /// <summary>The largest <see cref="FrameKind.ResponseChunk"/> body the relay will take; absent means <see cref="WireCodec.MaxChunkBytes"/>.</summary>
    [JsonPropertyName("maxChunkBytes")]
    public int? MaxChunkBytes { get; set; }

    // --- req ---

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>Path plus query, as aixman asked for it — e.g. <c>/view?filename=x.png</c>.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    // --- res ---

    [JsonPropertyName("status")]
    public int? Status { get; set; }

    // --- res-end ---

    /// <summary>
    /// True when the agent could not finish a streamed reply. The relay then
    /// cuts the aixman response off rather than ending it cleanly, so a
    /// truncated image is never mistaken for a whole one.
    /// </summary>
    [JsonPropertyName("aborted")]
    public bool? Aborted { get; set; }

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

    /// <summary>
    /// True while the node is running work — a customer's render, or the
    /// owner's own ComfyUI queue. Absent from agents that predate it, which the
    /// relay passes on as "unknown" rather than guessing "idle".
    /// </summary>
    [JsonPropertyName("busy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Busy { get; set; }

    /// <summary>Prompts waiting or running in the node's ComfyUI, when the agent reports it.</summary>
    [JsonPropertyName("queueRemaining")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? QueueRemaining { get; set; }

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

    /// <summary>
    /// Anything the reading build has no property for, kept and written back
    /// out unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The relay is a courier, not a reader: it takes this object off the
    /// agent's heartbeat and hands it to XMAN Studio. Without this it was also
    /// a filter — it deserialized into its own copy of this class and
    /// re-serialized, so every field newer than the deployed relay was silently
    /// dropped on the way through. <c>lanes</c> was added to the client, the
    /// client sent it, and the relay delivered telemetry without it.
    /// </para>
    /// <para>
    /// A node's telemetry should never need the relay to be redeployed to reach
    /// the far side. This is what makes the relay version-independent of the
    /// fleet, which matters most for the field it does not know about yet.
    /// </para>
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
