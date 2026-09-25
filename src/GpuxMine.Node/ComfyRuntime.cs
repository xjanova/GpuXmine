using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GpuxMine.Protocol;

namespace GpuxMine.Node;

public sealed record LocalReply(int Status, Dictionary<string, string> Headers, byte[] Body)
{
    public static LocalReply Json(int status, object payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
        return new LocalReply(status,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "application/json" },
            body);
    }
}

/// <summary>
/// The <c>stage</c> a refusal carries. aixman reads it to decide what to do
/// with the worker and the job: every one of these means "not now, ask again",
/// never "this node is broken".
/// </summary>
public static class ReadyStage
{
    /// <summary>The relay's answer when the node is not connected. The node itself never sends it.</summary>
    public const string Offline = "offline";

    /// <summary>The owner's rules say no right now: schedule, gaming, temperature, stopped.</summary>
    public const string Paused = "paused";

    /// <summary>No valid capability report, so no work at all until the machine is measured.</summary>
    public const string Unassessed = "unassessed";

    /// <summary>Already working: a customer's render, or the owner's own ComfyUI queue.</summary>
    public const string Busy = "busy";

    /// <summary>The owner pressed STOP. Finishing and handing over what it has, taking nothing new.</summary>
    public const string Draining = "draining";
}

/// <summary>Why the node is not taking work this second, for the readiness answer and the UI.</summary>
/// <param name="Stage">What aixman is told — see <see cref="ReadyStage"/>. Only read when <paramref name="Accept"/> is false.</param>
public sealed record AcceptDecision(bool Accept, string? Reason, string Stage = ReadyStage.Paused)
{
    public static readonly AcceptDecision Yes = new(true, null);
}

/// <summary>A file ComfyUI wrote or was given, named the way its own API names it.</summary>
/// <param name="Type"><c>output</c>, <c>input</c> or <c>temp</c> — which of ComfyUI's folders it lives in.</param>
public sealed record ComfyFile(string Filename, string Subfolder, string Type);

/// <summary>
/// Everything the node does with a request that arrived down the tunnel.
/// </summary>
/// <remarks>
/// <para>
/// Some of the paths aixman uses do not exist in ComfyUI at all — the rented
/// image serves them from a Python proxy that wraps it. A community node has no
/// such wrapper, so the agent answers them itself and forwards the rest:
/// </para>
/// <list type="bullet">
///   <item><c>/aixman/ready</c> — can this node take a job (also where the
///     owner's schedule and "yield when I use the PC" are enforced)</item>
///   <item><c>/aixman/progress</c> — how far the running render is</item>
///   <item><c>/aixman/log</c> — the only window into a node that misbehaves</item>
///   <item><c>/aixman/purge</c> — take a finished job's files and history off
///     the owner's machine once aixman has copied the result away</item>
/// </list>
/// <para>
/// Nothing else is forwarded unless it is on <see cref="TunnelAllowlist"/>.
/// The tunnel ends in the owner's own ComfyUI, and a relay that predates the
/// list would otherwise pass anything — ComfyUI-Manager's install routes
/// included — straight through to it.
/// </para>
/// <para>
/// New work (<c>POST /prompt</c>, <c>POST /upload/image</c>) is refused with
/// exactly the answer <c>/aixman/ready</c> would give. A readiness probe is a
/// snapshot, and the owner sitting down at the PC one second after it said yes
/// must still stop the job.
/// </para>
/// </remarks>
public sealed partial class ComfyRuntime : IAsyncDisposable
{
    [GeneratedRegex("sampler", RegexOptions.IgnoreCase)]
    private static partial Regex SamplerClass();

    /// <summary>
    /// ComfyUI sends a prompt's progress events only to the client id that
    /// submitted it, so the agent rewrites every submission to this one and
    /// listens as that client. Without the rewrite the events go to whoever
    /// aixman claimed to be and the node can never report a percentage.
    /// </summary>
    private readonly string _progressClientId = Guid.NewGuid().ToString();

    private readonly NodeOptions _options;
    private readonly HttpClient _http;
    private readonly ILoggerish _log;
    private readonly Func<AcceptDecision> _acceptGate;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// The machine's capability report, or the reason it has none yet.
    /// </summary>
    /// <remarks>
    /// Set by the host. Until it answers with a report, every readiness probe
    /// is refused: a machine that has not been measured is never sent work,
    /// because the alternative is discovering a card's limits on a customer's
    /// paid job.
    /// </remarks>
    public Func<(Assessment.NodeAssessment? Report, string? Reason)>? AssessmentSource { get; set; }

    /// <summary>
    /// Whether the owner offers this kind of work. Null offers everything the
    /// report says the machine can run.
    /// </summary>
    /// <remarks>
    /// Applied to what readiness advertises, so an owner who unticked video in
    /// Models &amp; Jobs is not listed as a video machine to the dispatcher.
    /// </remarks>
    public Func<string, bool>? OffersKind { get; set; }

    /// <summary>
    /// The node's own job ledger: which prompts came down the tunnel, what
    /// inputs they used, and which have been purged. Without one, only prompts
    /// this process saw submitted can be purged.
    /// </summary>
    public Storage.NodeStore? Ledger { get; set; }

    private readonly Lock _stateGate = new();
    private ProgressState _state = new();
    private readonly Dictionary<string, Dictionary<string, string>> _graphs = new(StringComparer.Ordinal);
    private volatile bool _listening;
    private readonly List<string> _recentLog = [];

    /// <summary>Raised as jobs move through the local runtime, for the history and the UI.</summary>
    public event Action<JobEvent>? Job;

    public sealed record JobEvent(string PromptId, JobStatus Status, int NodesTotal, string Kind, string? Filename, string? Error)
    {
        /// <summary>Files aixman uploaded that this prompt reads, on <see cref="JobStatus.Queued"/>; purged with it.</summary>
        public IReadOnlyList<ComfyFile> Inputs { get; init; } = [];

        /// <summary>
        /// Raised again, for a job that was not yet on record when it first
        /// happened, or settled from history rather than seen live. Recorded,
        /// but already said once in the log.
        /// </summary>
        public bool Replayed { get; init; }
    }

    private sealed class ProgressState
    {
        public string? PromptId;
        public DateTimeOffset Started = DateTimeOffset.UtcNow;
        public int Value;
        public int Max;
        public string? ProgressNode;
        public int NodesDone;
        public int SamplersDone;
        public DateTimeOffset? SamplerEnd;
        public bool Done;
        public bool Failed;
        public string? Error;
    }

    /// <param name="acceptGate">
    /// Asked on every readiness probe. Returning "no" makes the pool skip this
    /// node for new work without disconnecting it — the running job, if any,
    /// finishes; a customer's paid render is never killed for the owner's
    /// convenience.
    /// </param>
    public ComfyRuntime(NodeOptions options, ILoggerish log, Func<AcceptDecision>? acceptGate = null)
    {
        _options = options;
        _log = log;
        _acceptGate = acceptGate ?? (() => AcceptDecision.Yes);
        _http = Core.Net.NodeHttp.Create(TimeSpan.FromSeconds(150));
    }

    public bool Listening => _listening;

    // ---------------------------------------------------------------- routing

    public async Task<LocalReply> HandleAsync(string method, string pathAndQuery, Dictionary<string, string> headers, byte[] body, CancellationToken ct)
    {
        // Deny by default, before anything is parsed or forwarded. The relay
        // runs the same list, but a node talking to a relay that predates it
        // is protected only by this line.
        if (!TunnelAllowlist.IsAllowed(method, pathAndQuery, body))
        {
            NoteRefusal($"refused {method} {Clip(pathAndQuery)} — not on the tunnel allowlist");
            return LocalReply.Json(403, new { error = TunnelAllowlist.DeniedError });
        }

        string route = pathAndQuery.Split('?', 2)[0];

        if (route.StartsWith("/aixman/", StringComparison.Ordinal))
        {
            // The agent's own surface. None of it is ComfyUI's, so none of it
            // is forwarded — an unknown name is a 404 here, not a probe of
            // whatever the owner's ComfyUI happens to answer.
            switch (route)
            {
                case "/aixman/ready":
                    return await ReadinessAsync(ct);
                case "/aixman/progress":
                    return LocalReply.Json(200, Snapshot());
                case "/aixman/log":
                    lock (_stateGate) return LocalReply.Json(200, new { agent = string.Join('\n', _recentLog) });
                case "/aixman/purge":
                    return method == "POST"
                        ? await PurgeRequestAsync(pathAndQuery, body, ct)
                        : LocalReply.Json(405, new { error = "method-not-allowed" });
                default:
                    return LocalReply.Json(404, new { error = "unknown-route" });
            }
        }

        if (method == "POST" && route == "/prompt")
            return await SubmitAsync(method, pathAndQuery, headers, body, ct);

        if (method == "POST" && route == "/upload/image")
            return await UploadAsync(method, pathAndQuery, headers, body, ct);

        return await ForwardAsync(method, pathAndQuery, headers, body, ct);
    }

    private async Task<LocalReply> ReadinessAsync(CancellationToken ct)
    {
        var (refused, report) = await GateAsync(submitting: false, ct);
        if (refused is not null) return refused;

        try
        {
            using var response = await _http.GetAsync($"{_options.ComfyUrl.TrimEnd('/')}/system_stats", ct);
            if (!response.IsSuccessStatusCode)
                return LocalReply.Json(503, new { ready = false, stage = $"comfyui HTTP {(int)response.StatusCode}" });

            // The card torch sees right now, for the host to hold the report
            // against. Free: this answer was being read anyway.
            NoteDevice(await response.Content.ReadAsStringAsync(ct));

            var offered = report?.Capabilities.Where(c => c.CanRun && Offers(c.Kind)).ToArray() ?? [];

            // `auth: true` tells aixman the bearer token is being enforced. It is —
            // by the relay, before this request was ever put on the tunnel. Saying
            // false here would trip a SECURITY error on a node that is in fact gated.
            // The capability block travels with it so the dispatcher can pick a
            // job this card was measured able to finish, rather than any job at all.
            return LocalReply.Json(200, new
            {
                ready = true,
                auth = true,
                listening = _listening,
                queue_remaining = QueueRemaining,
                assessment = report is null ? null : new
                {
                    score = report.Score,
                    tier = report.Tier,
                    gpu = report.GpuName,
                    vram_mb = report.VramTotalMb,
                    measured_at = report.MeasuredAt,
                    // What the owner offers, not only what the card can do: a
                    // kind unticked in Models & Jobs is left out of all four.
                    can_run = offered.Select(c => c.Kind).ToArray(),
                    // `can_run` says the machine can do the work; `lanes` says
                    // whether anybody should be sitting there watching it. A
                    // dispatcher with only the first will eventually give a
                    // four-minute card to a customer expecting twelve seconds.
                    lanes = offered.ToDictionary(c => c.Kind, c => c.Lane),
                    provisional = report.Capabilities.Where(c => c.Provisional && Offers(c.Kind))
                        .Select(c => c.Kind).ToArray(),
                    seconds_per_unit = offered.ToDictionary(c => c.Kind, c => c.SecondsPerUnit),
                },
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return LocalReply.Json(503, new { ready = false, stage = "comfyui unreachable", detail = ex.Message });
        }
    }

    private bool Offers(string kind) => OffersKind?.Invoke(kind) ?? true;

    /// <summary>
    /// The one decision behind <c>/aixman/ready</c>, <c>POST /prompt</c> and
    /// <c>POST /upload/image</c>: null when new work may start, otherwise the
    /// refusal to send back.
    /// </summary>
    /// <remarks>
    /// In order of how long the answer is likely to hold. An unassessed node
    /// must not be dispatched to at all, however willing it says it is; a
    /// paused one will be back in a minute; a busy one as soon as its render
    /// ends. <c>stage</c>, not <c>failed</c>: aixman reads every one of these as
    /// "warming, check again" and keeps the worker. The owner sitting down at
    /// their PC is not a fault in the node.
    /// </remarks>
    private async Task<(LocalReply? Refused, Assessment.NodeAssessment? Report)> GateAsync(bool submitting, CancellationToken ct)
    {
        Assessment.NodeAssessment? report = null;
        if (AssessmentSource is { } askAssessment)
        {
            string? why;
            (report, why) = askAssessment();
            if (report is null)
                return (Refusal(503, ReadyStage.Unassessed, why), null);
        }

        AcceptDecision decision = _acceptGate();
        if (!decision.Accept)
            return (Refusal(503, decision.Stage, decision.Reason), report);

        // One customer render at a time. A second prompt behind the first
        // would sit in ComfyUI's queue with nobody measuring it, and time out
        // as a failure of this node when it was never started. A render still
        // executing counts even when nothing tracks it as a customer's any
        // more — the card is taken either way.
        if (HasTunnelWork || IsBusy)
        {
            return (Refusal(submitting ? 409 : 503, ReadyStage.Busy,
                "เครื่องกำลังทำงานของลูกค้าอยู่ — รับงานถัดไปเมื่อเสร็จ"), report);
        }

        // The owner's own work counts too. Their batch is theirs to run, and a
        // customer's job queued behind it would wait out its whole timeout.
        if (await ReadQueueAsync(ct) is { } queue && OwnersQueued(queue) is > 0 and var owners)
        {
            return (LocalReply.Json(503, new
            {
                ready = false,
                stage = ReadyStage.Busy,
                reason = $"ComfyUI ในเครื่องมีงานค้างในคิว {owners} งาน",
                queue_remaining = queue.Count,
            }), report);
        }

        return (null, report);
    }

    private static LocalReply Refusal(int status, string stage, string? reason) =>
        LocalReply.Json(status, new { ready = false, stage, reason });

    /// <summary>
    /// <c>POST /prompt</c>: the gate, then a one-at-a-time reservation, then
    /// ComfyUI.
    /// </summary>
    /// <remarks>
    /// The reservation is taken under the lock after the gate, not merely
    /// checked, because aixman's own one-job guard is not atomic with its
    /// claim: two submissions can arrive in the same second, and both would
    /// pass a check that only looked.
    /// </remarks>
    private async Task<LocalReply> SubmitAsync(string method, string pathAndQuery, Dictionary<string, string> headers, byte[] body, CancellationToken ct)
    {
        var (refused, _) = await GateAsync(submitting: true, ct);
        if (refused is not null)
        {
            NoteRefusal($"refused a job: {StageOf(refused)}");
            return refused;
        }

        lock (_stateGate)
        {
            if (_submitting > 0 || _tunnelPrompts.Count > 0)
                return Refusal(409, ReadyStage.Busy, "เครื่องกำลังทำงานของลูกค้าอยู่ — รับงานถัดไปเมื่อเสร็จ");
            _submitting++;
        }

        try
        {
            (byte[] rewritten, int nodeCount, string kind, HashSet<string> inputs) = RewritePrompt(body);
            LocalReply reply = await ForwardAsync(method, pathAndQuery, headers, rewritten, ct);

            string? promptId = PromptIdOf(reply);
            if (promptId is null) return reply;

            IReadOnlyList<ComfyFile> used = ClaimUploads(promptId, inputs);
            bool started;
            JobEvent? ended = null;
            lock (_stateGate)
            {
                // The ledger is the lasting record; this only covers a node
                // without one, so it is bounded rather than kept forever.
                if (_known.Count > 5000) _known.Clear();
                _known.Add(promptId);
                if (_graphs.Remove("", out var parked)) _graphs[promptId] = parked;

                // ComfyUI can start a prompt — and finish a fully cached one —
                // before its answer to the submission has been read here: the
                // progress socket is a separate connection and waits for
                // nobody. Those events found no job to update, so they are
                // replayed below, after the one that creates it.
                started = _state.PromptId == promptId;
                if (started && (_state.Done || _state.Failed))
                {
                    _lastTunnelFinished = DateTimeOffset.UtcNow;
                    ended = new JobEvent(promptId, _state.Failed ? JobStatus.Failed : JobStatus.Completed, 0, "job",
                        _lastOutput.GetValueOrDefault(promptId), _state.Failed ? _state.Error ?? "execution_error" : null)
                    { Replayed = true };
                }
                else
                {
                    // Tracked before the reservation is let go, so there is no
                    // instant in which neither says "busy".
                    _tunnelPrompts[promptId] = DateTimeOffset.UtcNow;
                }
            }

            Job?.Invoke(new JobEvent(promptId, JobStatus.Queued, nodeCount, kind, null, null) { Inputs = used });
            if (started) Job?.Invoke(new JobEvent(promptId, JobStatus.Running, nodeCount, kind, null, null) { Replayed = true });
            if (ended is not null) Job?.Invoke(ended);
            return reply;
        }
        finally
        {
            lock (_stateGate) _submitting--;
        }
    }

    private static string StageOf(LocalReply reply)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(reply.Body);
            return $"{root?["stage"]?.GetValue<string>()} — {root?["reason"]?.GetValue<string>()}";
        }
        catch
        {
            return $"HTTP {reply.Status}";
        }
    }

    private static string? PromptIdOf(LocalReply reply)
    {
        if (reply.Status is < 200 or >= 300) return null;
        try
        {
            return JsonNode.Parse(reply.Body)?["prompt_id"]?.GetValue<string>();
        }
        catch
        {
            // ComfyUI answered something that is not its usual JSON; the caller
            // already has the raw reply, and the history simply lacks this one.
            return null;
        }
    }

    private async Task<LocalReply> ForwardAsync(string method, string pathAndQuery, Dictionary<string, string> headers, byte[] body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), _options.ComfyUrl.TrimEnd('/') + pathAndQuery);

        if (body.Length > 0 || method is "POST" or "PUT" or "PATCH")
        {
            var content = new ByteArrayContent(body);
            if (headers.TryGetValue("Content-Type", out string? contentType) && !string.IsNullOrEmpty(contentType))
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            request.Content = content;
        }

        foreach (var header in headers)
        {
            if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            byte[] responseBody = await response.Content.ReadAsByteArrayAsync(ct);

            var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Content.Headers)
                responseHeaders[header.Key] = string.Join(", ", header.Value);

            return new LocalReply((int)response.StatusCode, responseHeaders, responseBody);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Note($"forward {method} {pathAndQuery} failed: {ex.Message}");
            return LocalReply.Json(502, new { error = "local runtime unreachable", detail = ex.Message });
        }
    }

    /// <summary>
    /// Points the submission's progress events at us and remembers the graph's
    /// shape, which is what turns raw sampler steps into a percentage.
    /// </summary>
    /// <remarks>
    /// Also collects every plain string the graph's nodes take as input, which
    /// is how a file aixman uploaded a moment ago is tied to the prompt that
    /// reads it — and purged with it.
    /// </remarks>
    private (byte[] Body, int NodeCount, string Kind, HashSet<string> Inputs) RewritePrompt(byte[] body)
    {
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            JsonNode? root = JsonNode.Parse(body);
            if (root is not JsonObject submitted) return (body, 0, "job", inputs);

            var classes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (submitted["prompt"] is JsonObject graph)
            {
                foreach (var node in graph)
                {
                    classes[node.Key] = (node.Value?["class_type"] as JsonValue)?.TryGetValue(out string? cls) == true ? cls : "";

                    if (node.Value?["inputs"] is JsonObject values)
                    {
                        foreach (var input in values)
                        {
                            if (input.Value is JsonValue v && v.TryGetValue(out string? text) && text.Length is > 0 and <= 512)
                                inputs.Add(text);
                        }
                    }
                }

                lock (_stateGate)
                {
                    // Keyed by prompt id once ComfyUI answers; until then park it
                    // under the empty key, which execution_start will claim.
                    _graphs[""] = classes;
                }
            }

            submitted["client_id"] = _progressClientId;
            return (Encoding.UTF8.GetBytes(submitted.ToJsonString()), classes.Count, KindOf(classes.Values), inputs);
        }
        catch (Exception ex)
        {
            // Forward it exactly as it came — the render still runs, only the
            // percentage goes unreported. Never fail a job over telemetry.
            Note($"could not rewrite /prompt client_id: {ex.Message}");
            return (body, 0, "job", inputs);
        }
    }

    /// <summary>A human word for the graph, from the node classes it uses. Heuristic, for the queue screen only.</summary>
    private static string KindOf(IEnumerable<string> classes)
    {
        var set = classes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (set.Any(c => c.Contains("Video", StringComparison.OrdinalIgnoreCase) || c.Contains("SVD", StringComparison.OrdinalIgnoreCase))) return "video";
        if (set.Any(c => c.Contains("Upscale", StringComparison.OrdinalIgnoreCase) || c.Contains("ESRGAN", StringComparison.OrdinalIgnoreCase))) return "upscale";
        if (set.Any(c => c.Contains("CLIPTextEncode", StringComparison.OrdinalIgnoreCase)) && set.Any(c => SamplerClass().IsMatch(c))) return "image";
        if (set.Any(c => c.Contains("Audio", StringComparison.OrdinalIgnoreCase))) return "audio";
        return "job";
    }

    // --------------------------------------------------------------- progress

    /// <summary>
    /// True while a render is executing. The updater asks before replacing the
    /// program: applying an update kills this process, and a job abandoned
    /// half-rendered is a job the customer paid for and the node does not get
    /// paid for.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="IsWorking"/>: a prompt that has been accepted
    /// but not yet started is not "busy" by this measure. Ask
    /// <see cref="IsWorking"/> before handing anything over.
    /// </remarks>
    public bool IsBusy
    {
        get
        {
            lock (_stateGate)
            {
                return _state.PromptId is not null && !_state.Done && !_state.Failed;
            }
        }
    }

    public object Snapshot()
    {
        lock (_stateGate)
        {
            if (_state.PromptId is null)
                return new { prompt_id = (string?)null, listening = _listening };

            var graph = _graphs.GetValueOrDefault(_state.PromptId) ?? [];
            int samplersTotal = graph.Values.Count(c => SamplerClass().IsMatch(c));
            bool progressIsSampler = _state.ProgressNode is not null
                && graph.TryGetValue(_state.ProgressNode, out string? cls)
                && SamplerClass().IsMatch(cls);

            return new
            {
                prompt_id = _state.PromptId,
                listening = _listening,
                elapsed = Math.Round((DateTimeOffset.UtcNow - _state.Started).TotalSeconds, 1),
                value = _state.Value,
                max = _state.Max,
                progress_is_sampler = progressIsSampler,
                nodes_total = graph.Count,
                nodes_done = _state.NodesDone,
                samplers_total = samplersTotal,
                samplers_done = _state.SamplersDone,
                since_sampling = _state.SamplerEnd is { } end
                    ? Math.Round((DateTimeOffset.UtcNow - end).TotalSeconds, 1)
                    : (double?)null,
                done = _state.Done,
                failed = _state.Failed,
            };
        }
    }

    /// <summary>0–100 for the UI, from the same numbers the pool sees.</summary>
    public int ProgressPercent
    {
        get
        {
            lock (_stateGate)
            {
                if (_state.PromptId is null) return 0;
                if (_state.Done) return 100;
                var graph = _graphs.GetValueOrDefault(_state.PromptId);
                int total = graph?.Count ?? 0;
                if (total == 0) return 0;
                double nodes = (double)_state.NodesDone / total;
                double within = _state.Max > 0 ? (double)_state.Value / _state.Max / total : 0;
                return (int)Math.Clamp((nodes + within) * 100, 0, 99);
            }
        }
    }

    /// <summary>
    /// Asks ComfyUI directly how a job ended.
    /// </summary>
    /// <remarks>
    /// The websocket is the fast path, not the record. Events are missed
    /// whenever the socket reconnects, and a fully-cached prompt can finish
    /// without emitting the pair we listen for at all — measured: three
    /// identical upscales in a row left the third stuck at "queued" in the
    /// ledger although ComfyUI had it as complete. A job the node did but never
    /// recorded is a job the owner is not paid for, so the ledger is settled
    /// from history, which is authoritative.
    /// </remarks>
    public async Task<(bool Done, bool Success, string? Filename, string? Error)> QueryHistoryAsync(string promptId, CancellationToken ct)
    {
        HistoryEntry entry = await HistoryAsync(promptId, ct);
        return entry.State == HistoryState.Done
            ? (true, entry.Success, entry.Filename, entry.Error)
            : (false, false, null, null);
    }

    /// <summary>What ComfyUI's history says about one prompt, with "could not ask" kept apart from "not there".</summary>
    /// <remarks>
    /// The difference matters: ComfyUI writes a history entry only when a
    /// prompt finishes, so a prompt that is absent is either still in the
    /// queue or lost — and a node that treated "ComfyUI did not answer" the
    /// same way would fail jobs every time ComfyUI was slow.
    /// </remarks>
    public async Task<HistoryEntry> HistoryAsync(string promptId, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"{_options.ComfyUrl.TrimEnd('/')}/history/{Uri.EscapeDataString(promptId)}", ct);
            if (!response.IsSuccessStatusCode) return HistoryEntry.Unknown;

            JsonNode? root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (root is not JsonObject) return HistoryEntry.Unknown;
            JsonNode? entry = root[promptId];
            if (entry is null) return new HistoryEntry(HistoryState.Absent);

            string? statusStr = (entry["status"]?["status_str"] as JsonValue)?.TryGetValue(out string? s) == true ? s : null;
            bool completed = (entry["status"]?["completed"] as JsonValue)?.TryGetValue(out bool c) == true && c;

            // Outputs are keyed by node id; any of them may hold the file.
            var files = new List<ComfyFile>();
            if (entry["outputs"] is JsonObject outputs)
            {
                foreach (var node in outputs)
                {
                    if (node.Value is not JsonObject buckets) continue;
                    foreach (var bucket in buckets)
                    {
                        if (bucket.Value is not JsonArray items) continue;
                        foreach (JsonNode? item in items)
                        {
                            if (item is not JsonObject file) continue;
                            if ((file["filename"] as JsonValue)?.TryGetValue(out string? name) != true || string.IsNullOrEmpty(name)) continue;
                            string subfolder = (file["subfolder"] as JsonValue)?.TryGetValue(out string? sub) == true ? sub ?? "" : "";
                            string type = (file["type"] as JsonValue)?.TryGetValue(out string? t) == true ? t ?? "output" : "output";
                            files.Add(new ComfyFile(name, subfolder, type));
                        }
                    }
                }
            }

            if (statusStr == "error")
                return new HistoryEntry(HistoryState.Done, Success: false, Error: "ComfyUI reported an execution error", Files: files);

            if (!completed && statusStr != "success") return new HistoryEntry(HistoryState.Pending, Files: files);

            // The first saved file is what the queue screen shows; outputs the
            // user never sees (a preview's temp file) come last in practice.
            string? filename = files.FirstOrDefault(f => f.Type == "output")?.Filename ?? files.FirstOrDefault()?.Filename;
            return new HistoryEntry(HistoryState.Done, Success: true, Filename: filename, Files: files);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return HistoryEntry.Unknown;
        }
    }

    /// <summary>Listens to ComfyUI as the submitting client, reconnecting for as long as the agent runs.</summary>
    public async Task TrackProgressAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        var wsUrl = new Uri(_options.ComfyUrl.Replace("http", "ws", StringComparison.Ordinal).TrimEnd('/')
                            + $"/ws?clientId={_progressClientId}");

        while (!linked.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(wsUrl, linked.Token);
                _listening = true;
                Note("listening for render progress");

                var buffer = new byte[32 * 1024];
                var message = new MemoryStream();
                while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, linked.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType == WebSocketMessageType.Text)
                        Consume(Encoding.UTF8.GetString(message.ToArray()));
                    message.SetLength(0);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Note($"progress socket dropped: {ex.Message}");
            }
            finally
            {
                _listening = false;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), linked.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal void Consume(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return; }

        string? type = root?["type"]?.GetValue<string>();
        JsonNode? data = root?["data"];
        if (type is null) return;

        JobEvent? announce = null;

        lock (_stateGate)
        {
            switch (type)
            {
                case "execution_start":
                {
                    string? promptId = data?["prompt_id"]?.GetValue<string>();
                    if (promptId is null) break;
                    if (_graphs.Remove("", out var parked)) _graphs[promptId] = parked;
                    _state = new ProgressState { PromptId = promptId, Started = DateTimeOffset.UtcNow };

                    // A node renders thousands of prompts over its life. Without
                    // this the graph map grows one entry per job until the
                    // process is restarted — a leak that only shows up on the
                    // machines that matter most, the ones that never go down.
                    // The last few are kept because aixman reads one final
                    // snapshot after a render completes.
                    while (_graphs.Count > 16)
                    {
                        string oldest = _graphs.Keys.First(k => k != promptId && k != "");
                        _graphs.Remove(oldest);
                    }

                    // The same leak, one filename per job. Only the one being
                    // rendered matters: execution_success reads it and is done.
                    foreach (string old in _lastOutput.Keys.Where(k => k != promptId).ToArray())
                        _lastOutput.Remove(old);

                    var g0 = _graphs.GetValueOrDefault(promptId);
                    announce = new JobEvent(promptId, JobStatus.Running, g0?.Count ?? 0, g0 is null ? "job" : KindOf(g0.Values), null, null);
                    break;
                }

                case "progress":
                {
                    _state.Value = (int)(data?["value"]?.GetValue<double>() ?? 0);
                    _state.Max = (int)(data?["max"]?.GetValue<double>() ?? 0);
                    _state.ProgressNode = data?["node"]?.GetValue<string>();
                    break;
                }

                case "executing":
                {
                    string? node = data?["node"]?.GetValue<string>();
                    if (node is null)
                    {
                        // ComfyUI signals "nothing left to execute" with a null
                        // node. The node that was running is finished too —
                        // without counting it here nodes_done stops one short
                        // of nodes_total on every render (seen as 1/2 in the
                        // first real test), and a progress bar that never
                        // reaches 100% reads as a hung job.
                        if (_state.ProgressNode is not null)
                        {
                            CountNodeDone(_state.ProgressNode);
                            _state.ProgressNode = null;
                        }
                        _state.Done = true;
                        break;
                    }
                    if (_state.ProgressNode is { } finished && finished != node)
                        CountNodeDone(finished);
                    _state.ProgressNode = node;
                    break;
                }

                case "executed":
                {
                    // Carries the output filename the moment a SaveImage node
                    // finishes — the one thing the queue screen wants to show.
                    string? filename = data?["output"]?["images"]?[0]?["filename"]?.GetValue<string>()
                        ?? data?["output"]?["gifs"]?[0]?["filename"]?.GetValue<string>();
                    if (filename is not null && _state.PromptId is not null)
                        _lastOutput[_state.PromptId] = filename;
                    break;
                }

                case "execution_success":
                {
                    _state.Done = true;
                    if (_state.PromptId is { } id)
                    {
                        FinishTunnel(id);
                        announce = new JobEvent(id, JobStatus.Completed, 0, "job", _lastOutput.GetValueOrDefault(id), null);
                    }
                    break;
                }

                case "execution_error":
                case "execution_interrupted":
                {
                    _state.Done = true;
                    _state.Failed = true;
                    string detail = data?.ToJsonString() ?? "";
                    Note($"render failed: {detail[..Math.Min(300, detail.Length)]}");
                    _state.Error = (data?["exception_message"] as JsonValue)?.TryGetValue(out string? message) == true ? message : type;
                    if (_state.PromptId is { } id)
                    {
                        FinishTunnel(id);
                        announce = new JobEvent(id, JobStatus.Failed, 0, "job", null, _state.Error);
                    }
                    break;
                }
            }
        }

        if (announce is not null) Job?.Invoke(announce);
    }

    private readonly Dictionary<string, string> _lastOutput = new(StringComparer.Ordinal);

    private void CountNodeDone(string node)
    {
        _state.NodesDone++;
        var graph = _state.PromptId is null ? null : _graphs.GetValueOrDefault(_state.PromptId);
        if (graph is not null && graph.TryGetValue(node, out string? cls) && SamplerClass().IsMatch(cls))
        {
            _state.SamplersDone++;
            _state.SamplerEnd = DateTimeOffset.UtcNow;
        }
    }

    private void Note(string line)
    {
        _log.Info($"[comfy] {line}");
        lock (_stateGate)
        {
            _recentLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {line}");
            if (_recentLog.Count > 200) _recentLog.RemoveRange(0, _recentLog.Count - 200);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _http.Dispose();
        _stopping.Dispose();
    }
}
