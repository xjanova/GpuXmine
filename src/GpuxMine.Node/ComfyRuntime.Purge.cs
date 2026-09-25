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
/// own history gives for that prompt, and only when they were written after
/// the job began.
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

    /// <summary>
    /// How long ComfyUI's folders are trusted once found. Short: the owner can
    /// close one install and start another on the same port, and a purge that
    /// went on resolving names against the first would delete that install's
    /// files — the owner's own, under the same counter-numbered names.
    /// </summary>
    internal TimeSpan FoldersTrustedFor { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How much older than its job a file may be and still be the job's. A
    /// render writes its outputs after the prompt was handed over; a minute
    /// covers the clock and file-system timestamp rounding.
    /// </summary>
    private static readonly TimeSpan OutputSkew = TimeSpan.FromMinutes(1);

    /// <summary>Uploads come before the prompt that reads them — by seconds, as aixman does it.</summary>
    private static readonly TimeSpan UploadLead = TimeSpan.FromHours(1);

    // ---------------------------------------------------------------- uploads

    private async Task<LocalReply> UploadAsync(string method, string pathAndQuery, Dictionary<string, string> headers, byte[] body, CancellationToken ct)
    {
        var (refused, _) = await GateAsync(submitting: false, ct);
        if (refused is not null)
        {
            NoteRefusal($"refused an upload: {StageOf(refused)}");
            return refused;
        }

        if (UploadRefusal(headers, body) is { } why)
        {
            NoteRefusal($"refused an upload: {why}");
            return LocalReply.Json(403, new { error = "upload-not-allowed", reason = why });
        }

        // On the node's own clock, like a submission: an upload ComfyUI has
        // written must be tracked, or nothing ever deletes it.
        LocalReply reply = await ForwardAsync(method, pathAndQuery, headers, body, _stopping.Token, refuseWhenUnreachable: true);
        RememberUpload(reply);
        return reply;
    }

    /// <summary>
    /// Why an upload must not reach ComfyUI, or null when it may: one file,
    /// named the way aixman names every file it uploads, into the top of the
    /// input folder.
    /// </summary>
    /// <remarks>
    /// ComfyUI's upload takes a folder (<c>type</c>: input, output or temp), a
    /// subfolder and <c>overwrite</c> from the caller. Unchecked, anyone
    /// holding the tunnel token could write over the owner's own renders by
    /// name. aixman sends none of type or subfolder, and names every file
    /// <c>aixman-…-{uuid}</c>, so a name with that prefix can only ever meet
    /// another of aixman's own files.
    /// </remarks>
    internal static string? UploadRefusal(IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        if (HeaderValue(headers, "Content-Type") is not { } contentType
            || !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var media)
            || !string.Equals(media.MediaType, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return "not multipart/form-data";
        }

        string? boundary = media.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 200) return "no multipart boundary";

        byte[] delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        byte[] nextPart = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        ReadOnlySpan<byte> data = body;

        int position = data.IndexOf(delimiter);
        if (position < 0) return "malformed multipart body";

        int images = 0;
        string? filename = null;
        string type = "", subfolder = "";
        for (int parts = 0; ; parts++)
        {
            if (parts > 32) return "too many multipart fields";
            position += delimiter.Length;
            if (data[position..].StartsWith("--"u8)) break;
            if (!data[position..].StartsWith("\r\n"u8)) return "malformed multipart body";
            position += 2;

            int headerLength = data[position..].IndexOf("\r\n\r\n"u8);
            if (headerLength < 0) return "malformed multipart body";
            string partHeaders = Encoding.UTF8.GetString(data.Slice(position, headerLength));
            position += headerLength + 4;

            int contentLength = data[position..].IndexOf(nextPart);
            if (contentLength < 0) return "malformed multipart body";
            ReadOnlySpan<byte> content = data.Slice(position, contentLength);
            position += contentLength + 2;

            (string? name, string? file) = FormField(partHeaders);
            switch (name)
            {
                case "image":
                    images++;
                    filename = file;
                    break;
                case "type":
                    type = Encoding.UTF8.GetString(content).Trim();
                    break;
                case "subfolder":
                    subfolder = Encoding.UTF8.GetString(content).Trim();
                    break;
            }
        }

        if (images != 1 || string.IsNullOrEmpty(filename)) return "one image file expected";
        if (!filename.StartsWith("aixman-", StringComparison.Ordinal)) return "only files named aixman-* may be uploaded";
        if (filename.Length > 200 || filename.Contains("..", StringComparison.Ordinal)
            || filename.Any(c => c is '/' or '\\' or ':' || c < 0x20 || c == 0x7f))
        {
            return "file name not allowed";
        }
        if (type is not ("" or "input")) return "uploads go to the input folder only";
        if (subfolder.Length > 0) return "uploads go to the top of the input folder only";
        return null;
    }

    /// <summary>The <c>name</c> and <c>filename</c> of one multipart part.</summary>
    private static (string? Name, string? Filename) FormField(string partHeaders)
    {
        foreach (string line in partHeaders.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon < 0 || !line[..colon].Trim().Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase)) continue;
            if (!System.Net.Http.Headers.ContentDispositionHeaderValue.TryParse(line[(colon + 1)..].Trim(), out var disposition)) return (null, null);
            return (disposition.Name?.Trim('"'), disposition.FileName?.Trim('"'));
        }
        return (null, null);
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
        List<(ComfyFile File, DateTimeOffset At)> stale;
        lock (_stateGate)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - olderThan;
            stale = _uploads.Where(u => u.At < cutoff).ToList();
            _uploads.RemoveAll(u => u.At < cutoff);
        }
        if (stale.Count == 0) return 0;

        ComfyFolders folders = await FoldersAsync(ct);
        int deleted = 0;
        foreach (var (file, at) in stale)
        {
            if (TryDelete(folders, file, at - OutputSkew, out _) == true) deleted++;
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

        // aixman purges once the result is safely copied away — this, and
        // not the node's own fallback purge, is what says a job was collected.
        if (!result.Unknown) NoteCollected(promptId);

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
    /// <remarks>
    /// Collection is not recorded here: the node's own fallback purge comes
    /// through this too, and must not tell a stop that aixman has the result.
    /// </remarks>
    public async Task<PurgeResult> PurgeAsync(string promptId, CancellationToken ct)
    {
        // Not ours, and so not ours to delete — whatever the id, nothing aixman
        // says reaches the owner's own work.
        if (!IsTunnelPrompt(promptId)) return new PurgeResult(0, false, 0, null) { Unknown = true };

        HistoryEntry entry = await HistoryAsync(promptId, ct);
        if (entry.State == HistoryState.Unknown) return new PurgeResult(0, false, 0, null) { Unreachable = true };
        if (entry.State == HistoryState.Pending) return new PurgeResult(0, false, 0, null) { Running = true };

        if (entry.State == HistoryState.Done)
        {
            // ComfyUI writes a prompt's history and takes it off the queue in
            // one step, so both at once means two prompts under one id — an
            // older entry, and a submission reusing its id that has not run
            // yet. Those files are the older one's, and not this job's to
            // delete. The node now picks every id it submits; this holds for
            // any row from before it did.
            if (await ReadQueueAsync(ct) is { } queue && queue.PromptIds.Contains(promptId))
                return new PurgeResult(0, false, 0, null) { Running = true };

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

        // Each file with the oldest it may be and still be this job's. Unknown
        // start (null) deletes no output: a counter-numbered name like
        // ComfyUI_00012_.png means nothing without it.
        DateTimeOffset? submitted = SubmittedAtOf(promptId);
        var files = new Dictionary<ComfyFile, DateTimeOffset?>();
        foreach (ComfyFile output in entry.Files ?? []) files.TryAdd(output, submitted - OutputSkew);

        var inputs = new List<ComfyFile>();
        lock (_stateGate)
        {
            if (_promptInputs.TryGetValue(promptId, out var claimed)) inputs.AddRange(claimed);
        }
        inputs.AddRange(FromLedger(l => l.InputsOf(promptId)) ?? []);
        foreach (ComfyFile input in inputs) files.TryAdd(input, submitted - UploadLead);

        int deleted = 0, skipped = 0;
        string? note = null;
        if (files.Count > 0)
        {
            ComfyFolders folders = await FoldersAsync(ct);
            foreach (var (file, notBefore) in files)
            {
                switch (TryDelete(folders, file, notBefore, out string? why))
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
            ForgetLocked(promptId);
            _graphs.Remove(promptId);
            _lastOutput.Remove(promptId);
            ForgetDeliverablesLocked(promptId);
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

    // ------------------------------------------------------------ benchmarks

    /// <summary>Prompts the node's own assessment sent to ComfyUI and not yet cleared away, with when each was sent.</summary>
    private readonly Dictionary<string, DateTimeOffset> _benchmarks = new(StringComparer.Ordinal);

    /// <summary>One clearing pass at a time: the assessment's own and the reconcile loop's can meet.</summary>
    private readonly SemaphoreSlim _benchmarkPass = new(1, 1);

    private bool _warnedBenchmarks;

    /// <summary>
    /// How long a benchmark prompt is tried for before it is let go — one
    /// ComfyUI never finishes, or whose history it will not give up.
    /// </summary>
    private static readonly TimeSpan BenchmarkWaitedFor = TimeSpan.FromDays(1);

    /// <summary>
    /// A prompt the node's own assessment sent, so what it wrote can be
    /// cleared away. Handed over once the run is over, never while it is
    /// still going: the assessment watches its prompt's history to see it
    /// finish, and a pass that deleted it first would leave it waiting out
    /// the clock on a render long done.
    /// </summary>
    /// <param name="sentAt">When it was sent; nothing older is deleted as its.</param>
    public void NoteBenchmark(string promptId, DateTimeOffset? sentAt = null)
    {
        if (!IsPlainId(promptId)) return;
        lock (_stateGate)
        {
            // Bounded: a ComfyUI that never answers must not grow this for
            // the life of the process. Two prompts per assessment.
            while (_benchmarks.Count >= 50)
                _benchmarks.Remove(_benchmarks.MinBy(b => b.Value).Key);
            _benchmarks.TryAdd(promptId, sentAt ?? DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Deletes the images the node's own benchmark prompts wrote into the
    /// owner's output folder, and their entries in ComfyUI's history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every assessment renders two small <c>gpuxmine_assess_*.png</c> into
    /// the owner's ComfyUI, and they used to stay there — two more with every
    /// re-assessment, and two more prompts the owner never ran in their history.
    /// </para>
    /// <para>
    /// Held to the purge's own rules — inside ComfyUI's folder, by the names
    /// ComfyUI's history gives for that prompt, written after the prompt was
    /// sent — and one more: the name carries the assessment's prefix. Nothing
    /// the owner made passes all four. A prompt still queued or running is
    /// kept for the next pass; one ComfyUI no longer holds anywhere is let go,
    /// as there is nothing of it left.
    /// </para>
    /// <para>
    /// Fails soft: ComfyUI not answering, a file it will not give up — none
    /// of it is the assessment's business, none of it throws, and the owner
    /// is told once.
    /// </para>
    /// </remarks>
    /// <returns>How many files were deleted.</returns>
    public async Task<int> PurgeBenchmarksAsync(CancellationToken ct)
    {
        lock (_stateGate)
        {
            // Free when there is nothing to clear: ComfyUI is not even asked.
            if (_benchmarks.Count == 0) return 0;
        }

        await _benchmarkPass.WaitAsync(ct);
        try
        {
            return await PurgeBenchmarksOnceAsync(ct);
        }
        finally
        {
            _benchmarkPass.Release();
        }
    }

    private async Task<int> PurgeBenchmarksOnceAsync(CancellationToken ct)
    {
        List<KeyValuePair<string, DateTimeOffset>> pending;
        lock (_stateGate)
        {
            if (_benchmarks.Count == 0) return 0;
            pending = [.. _benchmarks];
        }

        // Read before any history is. ComfyUI writes a prompt's history and
        // takes it off the queue in one step, so a prompt missing from both —
        // the queue first, the history after — has not merely finished in
        // between: it never ran, or ComfyUI lost it. Read the other way round,
        // one finishing between the two looked lost, and its image stayed.
        QueueSnapshot? queue = await ReadQueueAsync(ct);
        ComfyFolders? folders = null;
        int deleted = 0, skipped = 0, cleared = 0;
        string? note = null;

        foreach (var (promptId, sentAt) in pending)
        {
            bool expired = DateTimeOffset.UtcNow - sentAt > BenchmarkWaitedFor;
            HistoryEntry entry = await HistoryAsync(promptId, ct);

            // ComfyUI is not answering: every other one would fail the same way.
            if (entry.State == HistoryState.Unknown) break;

            if (entry.State != HistoryState.Done)
            {
                // No entry and not in the queue: refused, or lost with a
                // ComfyUI restart. Nothing of it is on disk to clear.
                bool gone = entry.State == HistoryState.Absent && queue is not null && !queue.PromptIds.Contains(promptId);
                if (gone || expired) ForgetBenchmark(promptId);
                continue;
            }

            folders ??= await FoldersAsync(ct);
            foreach (ComfyFile file in entry.Files ?? [])
            {
                if (!file.Filename.StartsWith(Assessment.Assessor.OutputPrefix + "_", StringComparison.Ordinal))
                {
                    skipped++;
                    note ??= $"ไม่ใช่ไฟล์ของการประเมินเครื่อง จึงไม่ลบ: {file.Filename}";
                    continue;
                }

                switch (TryDelete(folders, file, sentAt - OutputSkew, out string? why))
                {
                    case true: deleted++; break;
                    case null: skipped++; note ??= why; break;
                }
            }

            // Let go once the history is gone, whatever the files did — as a
            // customer purge does: a file left above is one this could not
            // delete on the next pass either. Kept when ComfyUI would not
            // delete it, and asked again next time, for a day.
            if (await DeleteHistoryAsync(promptId, ct))
            {
                ForgetBenchmark(promptId);
                cleared++;
            }
            else if (expired)
            {
                ForgetBenchmark(promptId);
            }
        }

        if (skipped > 0 && !_warnedBenchmarks)
        {
            _warnedBenchmarks = true;
            _log.Warn($"[warn] ลบภาพที่การประเมินเครื่องทิ้งไว้ใน ComfyUI ไม่ได้ {skipped} ไฟล์: {note}");
        }
        if (deleted > 0 || cleared > 0)
        {
            Note($"cleared the assessment's own renders: {deleted} file(s) deleted" +
                 $"{(skipped > 0 ? $", {skipped} left" : "")}, {cleared} history entr{(cleared == 1 ? "y" : "ies")} removed");
        }
        return deleted;
    }

    private void ForgetBenchmark(string promptId)
    {
        lock (_stateGate) _benchmarks.Remove(promptId);
    }

    /// <summary>True when deleted, false when there was nothing to delete, null when it could not be.</summary>
    /// <param name="notBefore">
    /// The oldest the file may be and still belong to the job; null when the
    /// job's start is not known. An older file under the same name is someone
    /// else's — the owner's own render from a different install, or the
    /// output a cached prompt merely points back at — and is left alone.
    /// </param>
    private static bool? TryDelete(ComfyFolders folders, ComfyFile file, DateTimeOffset? notBefore, out string? why)
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

            if (notBefore is not { } oldest)
            {
                why = "ไม่รู้ว่างานนี้เริ่มเมื่อไร — ไม่ลบไฟล์ที่อาจเป็นของเจ้าของเครื่อง";
                return null;
            }
            if (File.GetLastWriteTimeUtc(path) < oldest.UtcDateTime)
            {
                why = $"ไฟล์ {file.Filename} เก่ากว่างานนี้ — ไม่ใช่ไฟล์ของงาน จึงไม่ลบ";
                return null;
            }

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
        // Read again after a minute, found or not. It used to be kept for the
        // life of the process once found, and outlived the ComfyUI it was
        // read from — see FoldersTrustedFor.
        if (_folders is not null && DateTimeOffset.UtcNow - _foldersReadAt < FoldersTrustedFor) return _folders;

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
