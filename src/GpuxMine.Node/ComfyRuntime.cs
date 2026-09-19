using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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

/// <summary>Why the node is not taking work this second, for the readiness answer and the UI.</summary>
public sealed record AcceptDecision(bool Accept, string? Reason)
{
    public static readonly AcceptDecision Yes = new(true, null);
}

/// <summary>
/// Everything the node does with a request that arrived down the tunnel.
/// </summary>
/// <remarks>
/// Three of the paths aixman uses do not exist in ComfyUI at all — the rented
/// image serves them from a Python proxy that wraps it. A community node has no
/// such wrapper, so the agent answers them itself and forwards the rest:
/// <list type="bullet">
///   <item><c>/aixman/ready</c> — can this node take a job (also where the
///     owner's schedule and "yield when I use the PC" are enforced)</item>
///   <item><c>/aixman/progress</c> — how far the running render is</item>
///   <item><c>/aixman/log</c> — the only window into a node that misbehaves</item>
/// </list>
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

    private readonly Lock _stateGate = new();
    private ProgressState _state = new();
    private readonly Dictionary<string, Dictionary<string, string>> _graphs = new(StringComparer.Ordinal);
    private volatile bool _listening;
    private readonly List<string> _recentLog = [];

    /// <summary>Raised as jobs move through the local runtime, for the history and the UI.</summary>
    public event Action<JobEvent>? Job;

    public sealed record JobEvent(string PromptId, JobStatus Status, int NodesTotal, string Kind, string? Filename, string? Error);

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
        string route = pathAndQuery.Split('?', 2)[0];

        switch (route)
        {
            case "/aixman/ready":
                return await ReadinessAsync(ct);
            case "/aixman/progress":
                return LocalReply.Json(200, Snapshot());
            case "/aixman/log":
                lock (_stateGate) return LocalReply.Json(200, new { agent = string.Join('\n', _recentLog) });
        }

        if (method == "POST" && route == "/prompt")
        {
            (body, int nodeCount, string kind) = RewritePrompt(body);
            LocalReply reply = await ForwardAsync(method, pathAndQuery, headers, body, ct);
            AnnounceSubmission(reply, nodeCount, kind);
            return reply;
        }

        return await ForwardAsync(method, pathAndQuery, headers, body, ct);
    }

    private async Task<LocalReply> ReadinessAsync(CancellationToken ct)
    {
        // Assessment before everything else. A paused node is one that will be
        // back in a minute; an unassessed one must not be dispatched to at all,
        // however willing it says it is.
        Assessment.NodeAssessment? report = null;
        if (AssessmentSource is { } askAssessment)
        {
            string? why;
            (report, why) = askAssessment();
            if (report is null)
                return LocalReply.Json(503, new { ready = false, stage = "unassessed", reason = why });
        }

        AcceptDecision decision = _acceptGate();
        if (!decision.Accept)
        {
            // `stage`, not `failed`: aixman reads this as "warming, check again"
            // and keeps the worker. The owner sitting down at their PC is not a
            // fault in the node.
            return LocalReply.Json(503, new { ready = false, stage = "paused", reason = decision.Reason });
        }

        try
        {
            using var response = await _http.GetAsync($"{_options.ComfyUrl}/system_stats", ct);
            if (!response.IsSuccessStatusCode)
                return LocalReply.Json(503, new { ready = false, stage = $"comfyui HTTP {(int)response.StatusCode}" });

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
                assessment = report is null ? null : new
                {
                    score = report.Score,
                    tier = report.Tier,
                    gpu = report.GpuName,
                    vram_mb = report.VramTotalMb,
                    measured_at = report.MeasuredAt,
                    can_run = report.Capabilities.Where(c => c.CanRun).Select(c => c.Kind).ToArray(),
                    // `can_run` says the machine can do the work; `lanes` says
                    // whether anybody should be sitting there watching it. A
                    // dispatcher with only the first will eventually give a
                    // four-minute card to a customer expecting twelve seconds.
                    lanes = report.Capabilities.Where(c => c.CanRun)
                        .ToDictionary(c => c.Kind, c => c.Lane),
                    provisional = report.Capabilities.Where(c => c.Provisional)
                        .Select(c => c.Kind).ToArray(),
                    seconds_per_unit = report.Capabilities.Where(c => c.CanRun)
                        .ToDictionary(c => c.Kind, c => c.SecondsPerUnit),
                },
            });
        }
        catch (Exception ex)
        {
            return LocalReply.Json(503, new { ready = false, stage = "comfyui unreachable", detail = ex.Message });
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
    private (byte[] Body, int NodeCount, string Kind) RewritePrompt(byte[] body)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(body);
            if (root is not JsonObject submitted) return (body, 0, "job");

            var classes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (submitted["prompt"] is JsonObject graph)
            {
                foreach (var node in graph)
                    classes[node.Key] = node.Value?["class_type"]?.GetValue<string>() ?? "";

                lock (_stateGate)
                {
                    // Keyed by prompt id once ComfyUI answers; until then park it
                    // under the empty key, which execution_start will claim.
                    _graphs[""] = classes;
                }
            }

            submitted["client_id"] = _progressClientId;
            return (Encoding.UTF8.GetBytes(submitted.ToJsonString()), classes.Count, KindOf(classes.Values));
        }
        catch (Exception ex)
        {
            // Forward it exactly as it came — the render still runs, only the
            // percentage goes unreported. Never fail a job over telemetry.
            Note($"could not rewrite /prompt client_id: {ex.Message}");
            return (body, 0, "job");
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

    private void AnnounceSubmission(LocalReply reply, int nodeCount, string kind)
    {
        if (reply.Status is < 200 or >= 300) return;
        try
        {
            string? promptId = JsonNode.Parse(reply.Body)?["prompt_id"]?.GetValue<string>();
            if (promptId is null) return;
            Job?.Invoke(new JobEvent(promptId, JobStatus.Queued, nodeCount, kind, null, null));
        }
        catch
        {
            // ComfyUI answered something that is not its usual JSON; the caller
            // already has the raw reply, and the history simply lacks this one.
        }
    }

    // --------------------------------------------------------------- progress

    /// <summary>
    /// True while a render is in flight. The updater asks before replacing the
    /// program: applying an update kills this process, and a job abandoned
    /// half-rendered is a job the customer paid for and the node does not get
    /// paid for.
    /// </summary>
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
        try
        {
            using var response = await _http.GetAsync($"{_options.ComfyUrl.TrimEnd('/')}/history/{Uri.EscapeDataString(promptId)}", ct);
            if (!response.IsSuccessStatusCode) return (false, false, null, null);

            JsonNode? root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            JsonNode? entry = root?[promptId];
            if (entry is null) return (false, false, null, null);

            string? statusStr = entry["status"]?["status_str"]?.GetValue<string>();
            bool completed = entry["status"]?["completed"]?.GetValue<bool>() ?? false;

            if (statusStr == "error")
                return (true, false, null, "ComfyUI reported an execution error");

            if (!completed && statusStr != "success") return (false, false, null, null);

            // Outputs are keyed by node id; any of them may hold the file.
            string? filename = null;
            if (entry["outputs"] is JsonObject outputs)
            {
                foreach (var node in outputs)
                {
                    foreach (string bucket in (string[])["images", "gifs", "videos", "audio"])
                    {
                        if (node.Value?[bucket] is JsonArray items && items.Count > 0)
                        {
                            filename ??= items[0]?["filename"]?.GetValue<string>();
                        }
                    }
                }
            }

            return (true, true, filename, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, false, null, null);
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

    private void Consume(string json)
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
                        announce = new JobEvent(id, JobStatus.Completed, 0, "job", _lastOutput.GetValueOrDefault(id), null);
                    break;
                }

                case "execution_error":
                case "execution_interrupted":
                {
                    _state.Done = true;
                    _state.Failed = true;
                    string detail = data?.ToJsonString() ?? "";
                    Note($"render failed: {detail[..Math.Min(300, detail.Length)]}");
                    if (_state.PromptId is { } id)
                        announce = new JobEvent(id, JobStatus.Failed, 0, "job", null,
                            data?["exception_message"]?.GetValue<string>() ?? type);
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
