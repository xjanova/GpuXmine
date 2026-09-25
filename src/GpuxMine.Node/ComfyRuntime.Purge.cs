using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GpuxMine.Node;

/// <summary>Where ComfyUI keeps the three kinds of file its API names. Null where it could not be found.</summary>
public sealed record ComfyFolders(string? Output, string? Input, string? Temp)
{
    public static readonly ComfyFolders None = new(null, null, null);
}

/// <summary>What one purge did.</summary>
/// <param name="Files">Files deleted from disk.</param>
/// <param name="HistoryDeleted">ComfyUI no longer holds the prompt in its history.</param>
/// <param name="Skipped">Files named for the prompt that could not be deleted — folder unknown, or refused.</param>
/// <param name="Note">Why anything was skipped, for the caller and the log.</param>
public sealed record PurgeResult(int Files, bool HistoryDeleted, int Skipped, string? Note)
{
    /// <summary>Not a prompt that came down the tunnel. Nothing was touched.</summary>
    public bool Unknown { get; init; }

    /// <summary>Still rendering. Nothing was touched; ask again once it has finished.</summary>
    public bool Running { get; init; }

    /// <summary>ComfyUI could not be asked what the prompt produced. Nothing was touched.</summary>
    public bool Unreachable { get; init; }
}

/// <summary>
/// Taking a customer's job off the owner's machine once aixman has the result.
/// </summary>
/// <remarks>
/// <para>
/// The platform promises customers that their work does not stay on the
/// machine that rendered it. Before this, every prompt, output and uploaded
/// input sat in a stranger's ComfyUI folder and history for as long as that
/// stranger kept them.
/// </para>
/// <para>
/// Only prompts that came down the tunnel are ever touched — ones this
/// process accepted, or ones the node's ledger recorded — so nothing aixman
/// sends can delete the owner's own work. Files are deleted only inside the
/// folder ComfyUI itself would have written them to, by the names ComfyUI's
/// own history gives for that prompt.
/// </para>
/// </remarks>
public sealed partial class ComfyRuntime
{
    /// <summary>Files aixman uploaded and no prompt has claimed yet, with when they arrived.</summary>
    private readonly List<(ComfyFile File, DateTimeOffset At)> _uploads = [];

    /// <summary>Uploaded files a prompt reads, until that prompt is purged.</summary>
    private readonly Dictionary<string, List<ComfyFile>> _promptInputs = new(StringComparer.Ordinal);

    private ComfyFolders? _folders;
    private DateTimeOffset _foldersReadAt = DateTimeOffset.MinValue;
    private bool _warnedFolders;

    // ---------------------------------------------------------------- uploads

    private async Task<LocalReply> UploadAsync(string method, string pathAndQuery, Dictionary<string, string> headers, byte[] body, CancellationToken ct)
    {
        var (refused, _) = await GateAsync(submitting: false, ct);
        if (refused is not null)
        {
            NoteRefusal($"refused an upload: {StageOf(refused)}");
            return refused;
        }

        LocalReply reply = await ForwardAsync(method, pathAndQuery, headers, body, ct);
        RememberUpload(reply);
        return reply;
    }

    private void RememberUpload(LocalReply reply)
    {
        if (reply.Status is < 200 or >= 300) return;
        try
        {
            if (JsonNode.Parse(reply.Body) is not JsonObject root) return;
            if ((root["name"] as JsonValue)?.TryGetValue(out string? name) != true || string.IsNullOrEmpty(name)) return;
            string subfolder = (root["subfolder"] as JsonValue)?.TryGetValue(out string? sub) == true ? sub ?? "" : "";
            string type = (root["type"] as JsonValue)?.TryGetValue(out string? t) == true ? t ?? "input" : "input";

            lock (_stateGate)
            {
                _uploads.Add((new ComfyFile(name, subfolder, type), DateTimeOffset.UtcNow));
                // Bounded: a caller that uploads without ever submitting must
                // not grow this for the life of the process.
                if (_uploads.Count > 200) _uploads.RemoveRange(0, _uploads.Count - 200);
            }
        }
        catch
        {
            // Not ComfyUI's usual answer. The upload happened; it is just not tracked.
        }
    }

    /// <summary>
    /// Ties recent uploads to the prompt that reads them, by the names a
    /// LoadImage input can take: the bare name, <c>subfolder/name</c>, or
    /// ComfyUI's annotated <c>name [input]</c>.
    /// </summary>
    private IReadOnlyList<ComfyFile> ClaimUploads(string promptId, HashSet<string> inputs)
    {
        if (inputs.Count == 0) return [];

        lock (_stateGate)
        {
            var claimed = new List<ComfyFile>();
            for (int i = _uploads.Count - 1; i >= 0; i--)
            {
                ComfyFile file = _uploads[i].File;
                string withFolder = file.Subfolder.Length > 0 ? $"{file.Subfolder}/{file.Filename}" : file.Filename;
                if (inputs.Contains(file.Filename) || inputs.Contains(withFolder) || inputs.Contains($"{withFolder} [{file.Type}]"))
                {
                    claimed.Add(file);
                    _uploads.RemoveAt(i);
                }
            }

            if (claimed.Count > 0) _promptInputs[promptId] = claimed;
            return claimed;
        }
    }

    /// <summary>
    /// Deletes uploads no prompt ever claimed, once they are old enough that
    /// no prompt is going to.
    /// </summary>
    /// <returns>How many files were deleted.</returns>
    public async Task<int> SweepUnclaimedUploadsAsync(TimeSpan olderThan, CancellationToken ct)
    {
        List<ComfyFile> stale;
        lock (_stateGate)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - olderThan;
            stale = _uploads.Where(u => u.At < cutoff).Select(u => u.File).ToList();
            _uploads.RemoveAll(u => u.At < cutoff);
        }
        if (stale.Count == 0) return 0;

        ComfyFolders folders = await FoldersAsync(ct);
        int deleted = 0;
        foreach (ComfyFile file in stale)
        {
            if (TryDelete(folders, file, out _) == true) deleted++;
        }
        if (deleted > 0) Note($"deleted {deleted} unclaimed upload(s) older than {olderThan.TotalHours:0} h");
        return deleted;
    }

    // ------------------------------------------------------------------ purge

    private async Task<LocalReply> PurgeRequestAsync(string pathAndQuery, byte[] body, CancellationToken ct)
    {
        string? promptId = PromptIdFromBody(body) ?? PromptIdFromQuery(pathAndQuery);
        if (promptId is null || !IsPlainId(promptId))
            return LocalReply.Json(400, new { error = "prompt_id required" });

        PurgeResult result = await PurgeAsync(promptId, ct);
        // Both are "not yet", so both are statuses aixman asks again after: it
        // reads a 409 as a refusal that will never change, and would leave
        // the files for the node's own fallback purge.
        if (result.Running) return LocalReply.Json(503, new { error = "prompt-running", purged = 0 });
        if (result.Unreachable) return LocalReply.Json(502, new { error = "local runtime unreachable", purged = 0 });

        return LocalReply.Json(200, new
        {
            purged = result.Files,
            history = result.HistoryDeleted,
            skipped = result.Skipped,
            reason = result.Note,
        });
    }

    /// <summary>
    /// Deletes a finished customer prompt's output files, the inputs uploaded
    /// for it, and its entry in ComfyUI's history. Idempotent.
    /// </summary>
    public async Task<PurgeResult> PurgeAsync(string promptId, CancellationToken ct)
    {
        bool known;
        lock (_stateGate) known = _known.Contains(promptId) || _promptInputs.ContainsKey(promptId);
        if (!known) known = FromLedger(l => l.HasJob(promptId));

        // Not ours, and so not ours to delete — whatever the id, nothing aixman
        // says reaches the owner's own work.
        if (!known) return new PurgeResult(0, false, 0, null) { Unknown = true };

        HistoryEntry entry = await HistoryAsync(promptId, ct);
        if (entry.State == HistoryState.Unknown) return new PurgeResult(0, false, 0, null) { Unreachable = true };
        if (entry.State == HistoryState.Pending) return new PurgeResult(0, false, 0, null) { Running = true };

        if (entry.State == HistoryState.Done)
        {
            // Finished, whatever the progress socket did or did not report.
            // Settled here, before the history goes: it is the only record the
            // reconciler could settle the job from, and a job purged before
            // the reconciler reached it used to be written off as lost hours
            // later — done, delivered, and failed in the owner's ledger.
            SettleFromHistory(promptId, entry);
        }
        else
        {
            // No history: still queued, or ComfyUI lost it. The first is not
            // ours to touch yet; the second the reconciler writes off.
            bool running;
            lock (_stateGate)
            {
                running = _tunnelPrompts.ContainsKey(promptId)
                    || (_state.PromptId == promptId && !_state.Done && !_state.Failed);
            }
            if (running) return new PurgeResult(0, false, 0, null) { Running = true };
        }

        var files = new List<ComfyFile>(entry.Files ?? []);
        lock (_stateGate)
        {
            if (_promptInputs.TryGetValue(promptId, out var inputs)) files.AddRange(inputs);
        }
        files.AddRange(FromLedger(l => l.InputsOf(promptId)) ?? []);
        files = files.Distinct().ToList();

        int deleted = 0, skipped = 0;
        string? note = null;
        if (files.Count > 0)
        {
            ComfyFolders folders = await FoldersAsync(ct);
            foreach (ComfyFile file in files)
            {
                switch (TryDelete(folders, file, out string? why))
                {
                    case true: deleted++; break;
                    case null: skipped++; note ??= why; break;
                }
            }
        }

        bool historyDeleted = entry.State == HistoryState.Absent || await DeleteHistoryAsync(promptId, ct);

        // Recorded even when some files could not be reached: the history —
        // the customer's prompt text — is gone, and a purge that retried
        // forever would find nothing new to delete.
        FromLedger(l => { l.JobPurged(promptId); return true; });
        lock (_stateGate)
        {
            _promptInputs.Remove(promptId);
            _known.Remove(promptId);
            _graphs.Remove(promptId);
            _lastOutput.Remove(promptId);
            // aixman purges once the result is safely copied away, so this is
            // also the moment the job has been collected.
            _lastPurged = DateTimeOffset.UtcNow;
        }

        if (skipped > 0 && !_warnedFolders)
        {
            _warnedFolders = true;
            _log.Warn($"[warn] ลบไฟล์งานของลูกค้าไม่ได้ {skipped} ไฟล์: {note}");
        }
        Note($"purged {Short(promptId)}: {deleted} file(s) deleted{(skipped > 0 ? $", {skipped} skipped" : "")}, history {(historyDeleted ? "cleared" : "kept")}");

        return new PurgeResult(deleted, historyDeleted, skipped, note);
    }

    /// <summary>
    /// Closes a job from its history entry, if the runtime or the ledger still
    /// holds it open, exactly as the progress socket would have.
    /// </summary>
    private void SettleFromHistory(string promptId, HistoryEntry entry)
    {
        bool open = Settle(promptId, entry.Success);
        if (!open) open = FromLedger(l => l.JobIsOpen(promptId));
        if (!open) return;

        Job?.Invoke(new JobEvent(promptId, entry.Success ? JobStatus.Completed : JobStatus.Failed, 0, "job",
            entry.Filename, entry.Error));
    }

    /// <summary>True when deleted, false when there was nothing to delete, null when it could not be.</summary>
    private static bool? TryDelete(ComfyFolders folders, ComfyFile file, out string? why)
    {
        string? root = file.Type switch
        {
            "output" => folders.Output,
            "input" => folders.Input,
            "temp" => folders.Temp,
            _ => null,
        };

        if (root is null)
        {
            why = "ไม่รู้ว่าโฟลเดอร์ของ ComfyUI อยู่ที่ไหน — ตั้ง ComfyBaseDirectory ใน agent.json";
            return null;
        }

        string? path = ResolveInside(root, file.Subfolder, file.Filename);
        if (path is null)
        {
            why = $"ชื่อไฟล์ออกนอกโฟลเดอร์ของ ComfyUI: {file.Subfolder}/{file.Filename}";
            return null;
        }

        try
        {
            why = null;
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// The full path of a file ComfyUI named, or null when that name would
    /// land anywhere but inside <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Refused rather than normalised: <c>..</c>, a drive or a rooted path in
    /// either part, a separator in the file name. ComfyUI itself refuses to
    /// write outside its folders, but this deletes, and it does not take
    /// ComfyUI's word for that.
    /// </remarks>
    public static string? ResolveInside(string root, string? subfolder, string filename)
    {
        subfolder ??= "";
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(filename)) return null;
        if (filename.Contains('/') || filename.Contains('\\')) return null;

        foreach (string part in new[] { subfolder, filename })
        {
            if (part.Contains("..", StringComparison.Ordinal) || part.Contains(':')) return null;
            if (part.StartsWith('/') || part.StartsWith('\\') || Path.IsPathRooted(part)) return null;
            if (part.Any(c => c < 0x20 || c == 0x7f)) return null;
        }

        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, subfolder, filename));

        StringComparison compare = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(rootFull, compare) ? full : null;
    }

    private async Task<bool> DeleteHistoryAsync(string promptId, CancellationToken ct)
    {
        try
        {
            string body = JsonSerializer.Serialize(new { delete = new[] { promptId } });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{_options.ComfyUrl.TrimEnd('/')}/history", content, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private T? FromLedger<T>(Func<Storage.NodeStore, T> read)
    {
        if (Ledger is not { } ledger) return default;
        try
        {
            return read(ledger);
        }
        catch (Exception ex)
        {
            // The ledger is how purge tells a customer's prompt from the
            // owner's. Unreadable means "not known", which touches nothing.
            Note($"ledger unreadable during purge: {ex.Message}");
            return default;
        }
    }

    private static string? PromptIdFromBody(byte[] body)
    {
        if (body.Length == 0) return null;
        try
        {
            return (JsonNode.Parse(body)?["prompt_id"] as JsonValue)?.TryGetValue(out string? id) == true ? id : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? PromptIdFromQuery(string pathAndQuery)
    {
        string[] parts = pathAndQuery.Split('?', 2);
        if (parts.Length < 2) return null;
        foreach (string pair in parts[1].Split('&'))
        {
            string[] kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "prompt_id") return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    /// <summary>What a ComfyUI prompt id looks like: a UUID, or at least nothing that could be a path.</summary>
    private static bool IsPlainId(string value) =>
        value.Length is > 0 and <= 128
        && !value.Contains("..", StringComparison.Ordinal)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    // ---------------------------------------------------------------- folders

    /// <summary>
    /// Finds ComfyUI's output, input and temp folders: from the node's own
    /// settings first, then from how ComfyUI was started, then from where it
    /// says its custom nodes live.
    /// </summary>
    /// <remarks>
    /// ComfyUI has no API for deleting a file, so purge has to find the disk.
    /// It says where it is started from in <c>/system_stats</c> and where its
    /// folders are in <c>/internal/folder_paths</c>; between them a stock
    /// install, the portable build and the desktop app are all found without
    /// the owner configuring anything. A folder that does not exist is never
    /// returned — better "unknown" than a guess that deletes somewhere else.
    /// </remarks>
    public async Task<ComfyFolders> FoldersAsync(CancellationToken ct)
    {
        if (_folders is { Output: not null } found) return found;
        if (_folders is not null && DateTimeOffset.UtcNow - _foldersReadAt < TimeSpan.FromMinutes(10)) return _folders;

        string? baseDir = _options.ComfyBaseDirectory;
        string? output = _options.ComfyOutputDirectory;
        string? input = _options.ComfyInputDirectory;
        string? temp = null;

        IReadOnlyList<string> argv = await ReadArgvAsync(ct);
        baseDir ??= Flag(argv, "--base-directory");
        output ??= Flag(argv, "--output-directory");
        input ??= Flag(argv, "--input-directory");
        // ComfyUI puts its temp folder *inside* the one this names.
        if (Flag(argv, "--temp-directory") is { } tempParent) temp = Path.Combine(tempParent, "temp");

        baseDir ??= await CustomNodesParentAsync(ct);
        if (baseDir is null && argv.Count > 0 && Path.IsPathRooted(argv[0])
            && Path.GetFileName(argv[0]).Equals("main.py", StringComparison.OrdinalIgnoreCase))
        {
            baseDir = Path.GetDirectoryName(argv[0]);
        }

        if (baseDir is not null)
        {
            output ??= Path.Combine(baseDir, "output");
            input ??= Path.Combine(baseDir, "input");
            temp ??= Path.Combine(baseDir, "temp");
        }

        var folders = new ComfyFolders(Existing(output), Existing(input), Existing(temp));
        _folders = folders;
        _foldersReadAt = DateTimeOffset.UtcNow;
        return folders;
    }

    private static string? Existing(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try
        {
            string full = Path.GetFullPath(directory);
            return Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary><c>--flag value</c> or <c>--flag=value</c>, as ComfyUI's argparse accepts both.</summary>
    private static string? Flag(IReadOnlyList<string> argv, string name)
    {
        for (int i = 0; i < argv.Count; i++)
        {
            if (argv[i].Equals(name, StringComparison.Ordinal) && i + 1 < argv.Count) return argv[i + 1];
            if (argv[i].StartsWith(name + "=", StringComparison.Ordinal)) return argv[i][(name.Length + 1)..];
        }
        return null;
    }

    private async Task<IReadOnlyList<string>> ReadArgvAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"{_options.ComfyUrl.TrimEnd('/')}/system_stats", ct);
            if (!response.IsSuccessStatusCode) return [];
            JsonNode? root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (root?["system"]?["argv"] is not JsonArray argv) return [];
            return argv.Select(v => (v as JsonValue)?.TryGetValue(out string? s) == true ? s : null)
                .Where(s => s is not null).Select(s => s!).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    private async Task<string?> CustomNodesParentAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"{_options.ComfyUrl.TrimEnd('/')}/internal/folder_paths", ct);
            if (!response.IsSuccessStatusCode) return null;
            JsonNode? root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            JsonNode? first = root?["custom_nodes"] is JsonArray paths && paths.Count > 0 ? paths[0] : null;
            // Older builds list [paths, extensions]; newer ones list the paths.
            if (first is JsonArray nested) first = nested.Count > 0 ? nested[0] : null;
            return (first as JsonValue)?.TryGetValue(out string? customNodes) == true && !string.IsNullOrEmpty(customNodes)
                ? Path.GetDirectoryName(customNodes.TrimEnd('/', '\\'))
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
