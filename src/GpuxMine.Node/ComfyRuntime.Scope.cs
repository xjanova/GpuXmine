using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GpuxMine.Protocol;

namespace GpuxMine.Node;

/// <summary>
/// The parts of ComfyUI a tunnel caller reaches, held to the tunnel's own
/// prompts: their history, their place in the queue, their output files.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TunnelAllowlist"/> decides which routes may be used. It cannot
/// decide whose work a route touches — it sees a path, not the owner's
/// ComfyUI — and forwarded as they were, the routes aixman needs also listed
/// every prompt the owner ever ran, served their images by name, deleted their
/// history and interrupted their renders. That is the token holder the list
/// exists to stop, whoever it is: someone with a copy of aixman's database, or
/// until the relay splits tokens, the owner of any node.
/// </para>
/// <para>
/// So the node answers these itself, for the prompts that came down the tunnel
/// and nothing else. aixman uses each of them only that way already:
/// <c>/history/{id}</c> and <c>/queue</c> for a job it submitted, <c>/view</c> for
/// the files that job's history names, <c>POST /history</c> to delete that
/// job's entry, and never <c>/interrupt</c> or the whole history list.
/// </para>
/// </remarks>
public sealed partial class ComfyRuntime
{
    /// <summary>
    /// Output files the history of a customer prompt has named, and which
    /// prompt — everything <c>/view</c> will hand out.
    /// </summary>
    private readonly Dictionary<ComfyFile, string> _deliverables = new();

    private DateTimeOffset _lastDeliverableSearch = DateTimeOffset.MinValue;

    /// <summary>How often a <c>/view</c> of a file not on record may send the node looking through recent jobs for it.</summary>
    private static readonly TimeSpan DeliverableSearchEvery = TimeSpan.FromSeconds(5);

    private static readonly Dictionary<string, string> JsonContent =
        new(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "application/json" };

    // --------------------------------------------------------------- history

    /// <summary>
    /// <c>GET /history/{id}</c>: that prompt's entry if it came down the tunnel,
    /// otherwise the empty answer ComfyUI gives for an id it has never seen.
    /// </summary>
    /// <remarks>
    /// The files the entry names are noted as the ones <c>/view</c> may serve —
    /// this is the read aixman makes right before downloading them.
    /// </remarks>
    private async Task<LocalReply> HistoryRequestAsync(string promptId, string pathAndQuery, Dictionary<string, string> headers, CancellationToken ct)
    {
        if (!IsTunnelPrompt(promptId))
        {
            NoteRefusal($"answered GET /history/{Clip(promptId)} as empty — not a prompt that came down the tunnel");
            return LocalReply.Json(200, new { });
        }

        LocalReply reply = await ForwardAsync("GET", pathAndQuery, headers, [], ct);
        if (reply.Status is >= 200 and < 300)
        {
            try
            {
                NoteDeliverables(promptId, ReadHistory(JsonNode.Parse(reply.Body), promptId).Files);
            }
            catch (Exception)
            {
                // Not ComfyUI's usual answer. aixman gets it as it came; its
                // files are simply not on record for /view.
            }
        }
        return reply;
    }

    /// <summary>
    /// <c>POST /history {delete:[…]}</c>, with every id that is not a customer
    /// prompt taken out before ComfyUI sees it.
    /// </summary>
    private async Task<LocalReply> DeleteHistoryRequestAsync(byte[] body, CancellationToken ct)
    {
        // The allowlist has already held the body to {delete:[ids]} and
        // {clear:false}; the second is a no-op and is not passed on.
        var asked = new List<string>();
        try
        {
            if (JsonNode.Parse(body)?["delete"] is JsonArray ids)
            {
                foreach (JsonNode? id in ids)
                {
                    if ((id as JsonValue)?.TryGetValue(out string? value) == true && !string.IsNullOrEmpty(value)) asked.Add(value);
                }
            }
        }
        catch (Exception)
        {
            return LocalReply.Json(400, new { error = "invalid-body" });
        }

        List<string> ours = asked.Distinct(StringComparer.Ordinal).Where(IsTunnelPrompt).ToList();
        if (ours.Count < asked.Count)
            NoteRefusal($"refused to delete {asked.Count - ours.Count} history entr{(asked.Count - ours.Count == 1 ? "y" : "ies")} that did not come down the tunnel");

        // ComfyUI answers a delete with an empty 200, found or not.
        if (ours.Count == 0) return new LocalReply(200, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);

        return await ForwardAsync("POST", "/history", JsonContent, JsonSerializer.SerializeToUtf8Bytes(new { delete = ours }), ct);
    }

    // ----------------------------------------------------------------- queue

    /// <summary>
    /// <c>GET /queue</c> with only the tunnel's prompts left in it.
    /// </summary>
    /// <remarks>
    /// aixman asks it for one thing — whether its own prompt is still waiting.
    /// Every row also carries the whole prompt, and the owner's rows are the
    /// owner's prompts. The node's own count of the owner's queue is read
    /// separately and is not affected.
    /// </remarks>
    private async Task<LocalReply> QueueRequestAsync(string pathAndQuery, Dictionary<string, string> headers, CancellationToken ct)
    {
        LocalReply reply = await ForwardAsync("GET", pathAndQuery, headers, [], ct);
        if (reply.Status is < 200 or >= 300) return reply;

        var filtered = new JsonObject();
        try
        {
            JsonNode? root = JsonNode.Parse(reply.Body);
            foreach (string bucket in (string[])["queue_running", "queue_pending"])
            {
                var kept = new JsonArray();
                if (root?[bucket] is JsonArray rows)
                {
                    foreach (JsonNode? row in rows)
                    {
                        JsonNode? id = row is JsonArray fields && fields.Count > 1 ? fields[1] : row;
                        if ((id as JsonValue)?.TryGetValue(out string? promptId) == true && !string.IsNullOrEmpty(promptId)
                            && IsTunnelPrompt(promptId))
                        {
                            kept.Add(row!.DeepClone());
                        }
                    }
                }
                filtered[bucket] = kept;
            }
        }
        catch (Exception ex)
        {
            return LocalReply.Json(502, new { error = "unreadable queue from local runtime", detail = ex.Message });
        }

        return new LocalReply(200, new Dictionary<string, string>(JsonContent, StringComparer.OrdinalIgnoreCase),
            Encoding.UTF8.GetBytes(filtered.ToJsonString()));
    }

    // ------------------------------------------------------------------ view

    /// <summary>
    /// <c>GET /view</c> for a file a customer prompt's history names, and no
    /// other. Anything else is "not found", the same answer as a file that is
    /// not there.
    /// </summary>
    private async Task<LocalReply> ViewAsync(string pathAndQuery, Dictionary<string, string> headers, CancellationToken ct)
    {
        ComfyFile? file = ViewedFile(pathAndQuery);
        if (file is null || !(IsDeliverable(file) || await FindDeliverableAsync(file, ct)))
        {
            NoteRefusal($"refused GET /view {Clip(pathAndQuery)} — not a file of any job that came down the tunnel");
            return LocalReply.Json(404, new { error = "not-found" });
        }

        return await ForwardAsync("GET", pathAndQuery, headers, [], ct);
    }

    /// <summary>The file a <c>/view</c> query names, with ComfyUI's defaults filled in.</summary>
    private static ComfyFile? ViewedFile(string pathAndQuery)
    {
        string[] parts = pathAndQuery.Split('?', 2);
        if (parts.Length < 2) return null;

        string? filename = null;
        string subfolder = "", type = "output";
        foreach (string pair in parts[1].Split('&'))
        {
            if (pair.Length == 0) continue;
            string[] kv = pair.Split('=', 2);
            string value = kv.Length > 1 ? Unescape(kv[1]) : "";
            switch (Unescape(kv[0]))
            {
                case "filename": filename = value; break;
                case "subfolder": subfolder = value; break;
                case "type": type = value.Length == 0 ? "output" : value; break;
            }
        }
        return string.IsNullOrEmpty(filename) ? null : new ComfyFile(filename, subfolder, type);
    }

    /// <summary>A query value as aiohttp reads it: <c>+</c> is a space, then percent-decoding.</summary>
    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private bool IsDeliverable(ComfyFile file)
    {
        lock (_stateGate) return _deliverables.ContainsKey(file);
    }

    private void NoteDeliverables(string promptId, IReadOnlyList<ComfyFile>? files)
    {
        if (files is null || files.Count == 0) return;
        lock (_stateGate)
        {
            // Rebuilt on demand (FindDeliverableAsync), so a reset costs a
            // lookup, never a delivery.
            if (_deliverables.Count > 4000) _deliverables.Clear();
            foreach (ComfyFile file in files) _deliverables[file] = promptId;
        }
    }

    /// <summary>Called with the lock held, once a prompt's files are gone.</summary>
    private void ForgetDeliverablesLocked(string promptId)
    {
        foreach (ComfyFile file in _deliverables.Where(d => d.Value == promptId).Select(d => d.Key).ToArray())
            _deliverables.Remove(file);
    }

    /// <summary>
    /// Looks through the recent customer prompts' history for a file not on
    /// record — which happens when the node restarted between aixman reading a
    /// job's history and downloading its files.
    /// </summary>
    /// <remarks>
    /// At most once every few seconds, over a handful of jobs: a caller asking
    /// for names at random must not turn each guess into a dozen calls to the
    /// owner's ComfyUI.
    /// </remarks>
    private async Task<bool> FindDeliverableAsync(ComfyFile file, CancellationToken ct)
    {
        lock (_stateGate)
        {
            if (DateTimeOffset.UtcNow - _lastDeliverableSearch < DeliverableSearchEvery) return false;
            _lastDeliverableSearch = DateTimeOffset.UtcNow;
        }

        foreach (string promptId in RecentTunnelPrompts(limit: 10))
        {
            HistoryEntry entry = await HistoryAsync(promptId, ct);
            if (entry.State == HistoryState.Unknown) break;   // ComfyUI is not answering; the rest would fail too
            NoteDeliverables(promptId, entry.Files);
            if (IsDeliverable(file)) return true;
        }
        return false;
    }

    /// <summary>The newest customer prompts not yet purged: this process's, then the ledger's.</summary>
    private IReadOnlyList<string> RecentTunnelPrompts(int limit)
    {
        var ids = new List<string>();
        lock (_stateGate)
        {
            ids.AddRange(_acceptedAt.OrderByDescending(a => a.Value).Take(limit).Select(a => a.Key));
        }
        foreach (string id in FromLedger(l => l.UnpurgedJobs(DateTimeOffset.UtcNow - TimeSpan.FromDays(1), limit)) ?? [])
        {
            if (!ids.Contains(id)) ids.Add(id);
        }
        return ids.Take(limit * 2).ToList();
    }

    // ------------------------------------------------------------- interrupt

    /// <summary>
    /// <c>POST /interrupt</c>: stops the render only when it is a customer's,
    /// and names it, so a ComfyUI that honours the id stops nothing else.
    /// </summary>
    /// <remarks>
    /// ComfyUI's own route interrupts whatever is running when no id is given
    /// — the owner's render included. Anything that is not the tunnel's is
    /// answered as ComfyUI answers an interrupt with nothing to stop.
    /// </remarks>
    private async Task<LocalReply> InterruptAsync(byte[] body, CancellationToken ct)
    {
        string? running;
        lock (_stateGate)
        {
            running = _state.PromptId is { } current && !_state.Done && !_state.Failed ? current : null;
        }

        string? asked = PromptIdFromBody(body);
        bool ours = running is not null && IsTunnelPrompt(running) && (asked is null || asked == running);
        if (!ours)
        {
            NoteRefusal("ignored POST /interrupt — the render running is not one that came down the tunnel");
            return new LocalReply(200, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);
        }

        return await ForwardAsync("POST", "/interrupt", JsonContent, JsonSerializer.SerializeToUtf8Bytes(new { prompt_id = running }), ct);
    }
}
