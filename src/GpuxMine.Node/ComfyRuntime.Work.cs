using System.Text.Json.Nodes;

namespace GpuxMine.Node;

/// <summary>What ComfyUI's history says about one prompt.</summary>
public enum HistoryState
{
    /// <summary>ComfyUI did not answer, or answered something unreadable. Says nothing about the prompt.</summary>
    Unknown,

    /// <summary>No entry. ComfyUI writes one only when a prompt ends, so this is "still queued" or "lost".</summary>
    Absent,

    /// <summary>An entry that is not finished (the stand-in ComfyUI reports these).</summary>
    Pending,

    /// <summary>Finished, one way or the other.</summary>
    Done,
}

public sealed record HistoryEntry(
    HistoryState State,
    bool Success = false,
    string? Filename = null,
    string? Error = null,
    IReadOnlyList<ComfyFile>? Files = null)
{
    public static readonly HistoryEntry Unknown = new(HistoryState.Unknown);
}

/// <summary>ComfyUI's <c>/queue</c>: how many prompts are running or waiting, and which.</summary>
public sealed record QueueSnapshot(int Count, IReadOnlySet<string> PromptIds);

/// <summary>
/// The tunnel's work, as the node tracks it: which customer prompts are in
/// flight, what ComfyUI's own queue holds, and which card is in the machine.
/// </summary>
public sealed partial class ComfyRuntime
{
    /// <summary>Loopback calls made on the way to answering aixman. Short: a probe that hangs is worse than one that says "unknown".</summary>
    private static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Customer prompts accepted down the tunnel and not yet finished, with when they were accepted.</summary>
    private readonly Dictionary<string, DateTimeOffset> _tunnelPrompts = new(StringComparer.Ordinal);

    /// <summary>Every prompt id this process accepted down the tunnel, so purge can tell ours from the owner's.</summary>
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    /// <summary>How many consecutive checks found a tracked prompt in neither the queue nor the history.</summary>
    private readonly Dictionary<string, int> _misses = new(StringComparer.Ordinal);

    /// <summary>A <c>POST /prompt</c> between the gate and ComfyUI's answer.</summary>
    private int _submitting;

    private DateTimeOffset _lastTunnelFinished = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPurged = DateTimeOffset.MinValue;
    private int? _queueRemaining;
    private string? _gpuHash;

    private DateTimeOffset _lastRefusalNote = DateTimeOffset.MinValue;
    private int _refusalsSuppressed;

    /// <summary>A customer prompt is being submitted, is queued, or is rendering.</summary>
    public bool HasTunnelWork
    {
        get
        {
            lock (_stateGate) return _submitting > 0 || _tunnelPrompts.Count > 0;
        }
    }

    /// <summary>
    /// Anything a handover has to wait for: a customer's prompt anywhere
    /// between accepted and finished, or a render executing.
    /// </summary>
    public bool IsWorking => HasTunnelWork || IsBusy;

    /// <summary>When the last customer prompt finished. aixman fetches the result after this, so a stop waits a while beyond it.</summary>
    public DateTimeOffset LastTunnelFinishedAt
    {
        get
        {
            lock (_stateGate) return _lastTunnelFinished;
        }
    }

    /// <summary>
    /// aixman has purged a job since the last customer render ended — which it
    /// does only after copying the result away, so there is nothing left for
    /// it to collect.
    /// </summary>
    /// <remarks>
    /// Lets a stop end as soon as delivery is done, instead of always waiting
    /// out the grace. An aixman that predates <c>/aixman/purge</c> never says
    /// so, and gets the grace.
    /// </remarks>
    public bool CollectedSinceLastFinish
    {
        get
        {
            lock (_stateGate)
            {
                return _lastTunnelFinished != DateTimeOffset.MinValue
                    && _lastPurged >= _lastTunnelFinished
                    && _tunnelPrompts.Count == 0;
            }
        }
    }

    /// <summary>Prompts in ComfyUI's queue when last read, owner's and customers' alike. Null when ComfyUI could not be asked.</summary>
    public int? QueueRemaining
    {
        get
        {
            lock (_stateGate) return _queueRemaining;
        }
    }

    /// <summary>
    /// Which card torch reported the last time ComfyUI was asked, as
    /// <see cref="Assessment.NodeAssessment.GpuHashOf"/> hashes it. Null until
    /// ComfyUI has answered once.
    /// </summary>
    public string? CurrentGpuHash
    {
        get
        {
            lock (_stateGate) return _gpuHash;
        }
    }

    /// <summary>The customer prompts the runtime believes are still open — accepted, queued or rendering.</summary>
    public IReadOnlyList<string> TrackedPrompts()
    {
        lock (_stateGate)
        {
            var ids = new List<string>(_tunnelPrompts.Keys);
            if (_state.PromptId is { } current && !_state.Done && !_state.Failed && !ids.Contains(current))
                ids.Add(current);
            return ids;
        }
    }

    /// <summary>Called with the lock held, when ComfyUI says a prompt has ended.</summary>
    private void FinishTunnel(string promptId)
    {
        if (_tunnelPrompts.Remove(promptId)) _lastTunnelFinished = DateTimeOffset.UtcNow;
        _misses.Remove(promptId);
    }

    /// <summary>
    /// Closes a prompt the progress socket never reported as finished — from
    /// history, or because ComfyUI no longer has it anywhere.
    /// </summary>
    /// <remarks>
    /// Without this, a ComfyUI that died mid-render left <see cref="IsBusy"/>
    /// true for the life of the process: re-assessment and updates both wait
    /// for it, an unassessed node gets no work, and with no work nothing ever
    /// cleared it.
    /// </remarks>
    /// <returns>True when the runtime was still holding the prompt open.</returns>
    public bool Settle(string promptId, bool success)
    {
        lock (_stateGate)
        {
            bool tracked = _tunnelPrompts.Remove(promptId);
            if (tracked) _lastTunnelFinished = DateTimeOffset.UtcNow;
            _misses.Remove(promptId);

            bool current = _state.PromptId == promptId && !_state.Done && !_state.Failed;
            if (current)
            {
                _state.Done = true;
                _state.Failed = !success;
            }
            return tracked || current;
        }
    }

    /// <summary>Counts one more check that found a tracked prompt nowhere in ComfyUI. Returns the running count.</summary>
    public int NoteMissing(string promptId)
    {
        lock (_stateGate)
        {
            int misses = _misses.GetValueOrDefault(promptId) + 1;
            _misses[promptId] = misses;
            return misses;
        }
    }

    /// <summary>The prompt turned up again (still queued), so earlier misses were a race, not a loss.</summary>
    public void NoteFound(string promptId)
    {
        lock (_stateGate) _misses.Remove(promptId);
    }

    /// <summary>Records a customer prompt as accepted, exactly as a successful <c>POST /prompt</c> does.</summary>
    internal void TrackTunnelPrompt(string promptId)
    {
        lock (_stateGate)
        {
            _tunnelPrompts[promptId] = DateTimeOffset.UtcNow;
            _known.Add(promptId);
        }
    }

    // ------------------------------------------------------------------ queue

    /// <summary>
    /// Reads ComfyUI's queue. Null when ComfyUI could not be asked — which is
    /// not the same as an empty queue, and callers must not treat it as one
    /// where that would fail or purge something.
    /// </summary>
    public async Task<QueueSnapshot?> ReadQueueAsync(CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(QueueTimeout);
        try
        {
            using var response = await _http.GetAsync($"{_options.ComfyUrl.TrimEnd('/')}/queue", bounded.Token);
            if (!response.IsSuccessStatusCode)
            {
                SetQueue(null);
                return null;
            }

            JsonNode? root = JsonNode.Parse(await response.Content.ReadAsStringAsync(bounded.Token));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            foreach (string bucket in (string[])["queue_running", "queue_pending"])
            {
                if (root?[bucket] is not JsonArray items) continue;
                foreach (JsonNode? item in items)
                {
                    count++;
                    // ComfyUI lists [number, prompt_id, prompt, extra, outputs];
                    // the stand-in lists bare ids. Either way, the id.
                    JsonNode? id = item is JsonArray row && row.Count > 1 ? row[1] : item;
                    if ((id as JsonValue)?.TryGetValue(out string? promptId) == true && !string.IsNullOrEmpty(promptId))
                        ids.Add(promptId);
                }
            }

            SetQueue(count);
            return new QueueSnapshot(count, ids);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            SetQueue(null);
            return null;
        }
    }

    private void SetQueue(int? count)
    {
        lock (_stateGate) _queueRemaining = count;
    }

    /// <summary>
    /// How much of the queue is the owner's own work: everything except the
    /// customer prompts this node accepted.
    /// </summary>
    /// <remarks>
    /// ComfyUI sends a prompt's success event a moment before it takes the
    /// prompt off its running list. Counting that finished customer render as
    /// the owner's would refuse the next job for work that is already done —
    /// and the prompts still open are refused for separately, as ours.
    /// </remarks>
    private int OwnersQueued(QueueSnapshot queue)
    {
        lock (_stateGate)
        {
            int ours = queue.PromptIds.Count(_known.Contains);
            return Math.Max(0, queue.Count - ours);
        }
    }

    /// <summary>Keeps <see cref="QueueRemaining"/> fresh for the heartbeat, which cannot wait on a network call.</summary>
    public async Task WatchQueueAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        while (!linked.IsCancellationRequested)
        {
            await ReadQueueAsync(linked.Token);
            try { await Task.Delay(TimeSpan.FromSeconds(5), linked.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ----------------------------------------------------------------- device

    /// <summary>Asks ComfyUI which card torch is using, and remembers it. Null when ComfyUI did not answer.</summary>
    public async Task<string?> ReadGpuHashAsync(CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await _http.GetAsync($"{_options.ComfyUrl.TrimEnd('/')}/system_stats", bounded.Token);
            if (!response.IsSuccessStatusCode) return null;
            NoteDevice(await response.Content.ReadAsStringAsync(bounded.Token));
            return CurrentGpuHash;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Takes the card's name and memory out of a <c>/system_stats</c> answer.</summary>
    private void NoteDevice(string systemStats)
    {
        try
        {
            JsonNode? device = JsonNode.Parse(systemStats)?["devices"]?[0];
            string? name = (device?["name"] as JsonValue)?.TryGetValue(out string? n) == true ? n : null;
            long bytes = (device?["vram_total"] as JsonValue)?.TryGetValue(out long b) == true ? b : 0;
            string? hash = Assessment.NodeAssessment.GpuHashOf(name, (int)(bytes / 1024 / 1024));
            if (hash is null) return;
            lock (_stateGate) _gpuHash = hash;
        }
        catch
        {
            // Not knowing the card this once changes nothing; the last answer stands.
        }
    }

    // -------------------------------------------------------------------- log

    /// <summary>
    /// Refusals are logged, but not one line per request: whoever is knocking
    /// on a closed door can knock a thousand times a minute, and each line is
    /// a row in the owner's database.
    /// </summary>
    private void NoteRefusal(string line)
    {
        int suppressed;
        lock (_stateGate)
        {
            if (DateTimeOffset.UtcNow - _lastRefusalNote < TimeSpan.FromSeconds(10))
            {
                _refusalsSuppressed++;
                return;
            }
            _lastRefusalNote = DateTimeOffset.UtcNow;
            suppressed = _refusalsSuppressed;
            _refusalsSuppressed = 0;
        }
        Note(suppressed > 0 ? $"{line} (and {suppressed} more refused since the last line)" : line);
    }

    private static string Clip(string text) => text.Length > 160 ? text[..160] + "…" : text;
}
