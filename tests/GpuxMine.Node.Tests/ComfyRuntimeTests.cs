using System.Text.Json.Nodes;
using GpuxMine.Node.Assessment;
using GpuxMine.Protocol;

namespace GpuxMine.Node.Tests;

/// <summary>
/// What the node does with a request that came down the tunnel: the
/// allowlist, the gate on new work, one customer prompt at a time, and purge.
/// </summary>
public class ComfyRuntimeTests
{
    private static NodeAssessment Report(params string[] kinds) => new()
    {
        AgentVersion = "test",
        MeasuredAt = DateTimeOffset.UtcNow,
        GpuName = "RTX 3060",
        VramTotalMb = 12288,
        Score = 800,
        Tier = "gold",
        Capabilities = kinds.Select(k => new Capability { Kind = k, CanRun = true, Lane = "full", SecondsPerUnit = 10 }).ToList(),
    };

    private static ComfyRuntime Runtime(FakeComfy comfy, Func<AcceptDecision>? gate = null, NodeOptions? options = null, NodeAssessment? report = null)
    {
        var runtime = new ComfyRuntime(options ?? new NodeOptions { ComfyUrl = comfy.Url }, new NullLog(), gate);
        NodeAssessment assessed = report ?? Report("image");
        runtime.AssessmentSource = () => (assessed, null);
        return runtime;
    }

    private static Task<LocalReply> Send(ComfyRuntime runtime, string method, string path, byte[]? body = null) =>
        runtime.HandleAsync(method, path, Http.NoHeaders, body ?? [], CancellationToken.None);

    // ------------------------------------------------------------- allowlist

    [Theory]
    [InlineData("POST", "/customnode/install")]
    [InlineData("GET", "/userdata/comfy.settings.json")]
    [InlineData("POST", "/free")]
    [InlineData("GET", "/view?filename=../../secrets.txt&type=output")]
    [InlineData("GET", "/api/manager/queue/start")]
    [InlineData("DELETE", "/history/abc")]
    public async Task Anything_off_the_allowlist_is_refused_and_never_reaches_ComfyUI(string method, string path)
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);

        LocalReply reply = await Send(runtime, method, path, Http.Json(new { }));

        Assert.Equal(403, reply.Status);
        Assert.Equal(TunnelAllowlist.DeniedError, Http.Body(reply)["error"]!.GetValue<string>());
        Assert.Empty(comfy.Hits);
    }

    [Fact]
    public async Task Clearing_the_owners_whole_history_is_refused()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);

        LocalReply reply = await Send(runtime, "POST", "/history", Http.Json(new { clear = true }));

        Assert.Equal(403, reply.Status);
        Assert.False(comfy.Reached("POST /history"));
    }

    [Fact]
    public async Task What_aixman_uses_is_forwarded()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);

        Assert.Equal(200, (await Send(runtime, "GET", "/object_info")).Status);
        Assert.Equal(200, (await Send(runtime, "GET", "/object_info/KSampler")).Status);
        Assert.Equal(200, (await Send(runtime, "GET", "/view?filename=aix_00001_.png&type=output")).Status);
        Assert.True(comfy.Reached("GET /object_info"));
        Assert.True(comfy.Reached("GET /view"));
    }

    [Fact]
    public async Task An_unknown_aixman_route_is_answered_here_not_forwarded()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);

        LocalReply reply = await Send(runtime, "GET", "/aixman/secret");

        Assert.Equal(404, reply.Status);
        Assert.Empty(comfy.Hits);
    }

    // ------------------------------------------------------------------ gate

    [Theory]
    [InlineData(ReadyStage.Paused, "เจ้าของกำลังใช้เครื่อง")]
    [InlineData(ReadyStage.Draining, "กำลังหยุดแชร์")]
    public async Task A_prompt_is_refused_with_the_same_answer_readiness_gives(string stage, string reason)
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy, () => new AcceptDecision(false, reason, stage));

        LocalReply ready = await Send(runtime, "GET", "/aixman/ready");
        LocalReply prompt = await Send(runtime, "POST", "/prompt", Http.ImagePrompt());
        LocalReply upload = await Send(runtime, "POST", "/upload/image", [1, 2, 3]);

        foreach (LocalReply reply in new[] { ready, prompt, upload })
        {
            Assert.Equal(503, reply.Status);
            JsonNode body = Http.Body(reply);
            Assert.False(body["ready"]!.GetValue<bool>());
            Assert.Equal(stage, body["stage"]!.GetValue<string>());
            Assert.Equal(reason, body["reason"]!.GetValue<string>());
        }

        Assert.False(comfy.Reached("POST /prompt"));
        Assert.False(comfy.Reached("POST /upload/image"));
        Assert.False(runtime.HasTunnelWork);
    }

    [Fact]
    public async Task An_unassessed_node_takes_no_prompt()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = new ComfyRuntime(new NodeOptions { ComfyUrl = comfy.Url }, new NullLog());
        runtime.AssessmentSource = () => (null, "ยังไม่ได้ประเมินเครื่อง");

        LocalReply reply = await Send(runtime, "POST", "/prompt", Http.ImagePrompt());

        Assert.Equal(503, reply.Status);
        Assert.Equal(ReadyStage.Unassessed, Http.Body(reply)["stage"]!.GetValue<string>());
        Assert.False(comfy.Reached("POST /prompt"));
    }

    [Fact]
    public async Task The_owners_own_queue_makes_the_node_busy()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);
        comfy.Queue["owners-batch-1"] = true;
        comfy.Queue["owners-batch-2"] = true;

        LocalReply ready = await Send(runtime, "GET", "/aixman/ready");
        LocalReply prompt = await Send(runtime, "POST", "/prompt", Http.ImagePrompt());

        Assert.Equal(503, ready.Status);
        Assert.Equal(ReadyStage.Busy, Http.Body(ready)["stage"]!.GetValue<string>());
        Assert.Equal(2, Http.Body(ready)["queue_remaining"]!.GetValue<int>());
        Assert.Equal(503, prompt.Status);
        Assert.False(comfy.Reached("POST /prompt"));
        Assert.Equal(2, runtime.QueueRemaining);
    }

    [Fact]
    public async Task A_second_prompt_while_a_customer_render_runs_gets_409()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);

        LocalReply first = await Send(runtime, "POST", "/prompt", Http.ImagePrompt());
        Assert.Equal(200, first.Status);
        string promptId = Http.Body(first)["prompt_id"]!.GetValue<string>();
        Assert.True(runtime.HasTunnelWork);

        LocalReply second = await Send(runtime, "POST", "/prompt", Http.ImagePrompt());
        Assert.Equal(409, second.Status);
        Assert.Equal(ReadyStage.Busy, Http.Body(second)["stage"]!.GetValue<string>());

        LocalReply ready = await Send(runtime, "GET", "/aixman/ready");
        Assert.Equal(503, ready.Status);
        Assert.Equal(ReadyStage.Busy, Http.Body(ready)["stage"]!.GetValue<string>());

        // The render ends: the node takes work again.
        runtime.Consume(Http.Started(promptId));
        runtime.Consume(Http.Succeeded(promptId));
        Assert.False(runtime.HasTunnelWork);
        Assert.True(runtime.LastTunnelFinishedAt > DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(200, (await Send(runtime, "POST", "/prompt", Http.ImagePrompt())).Status);
        Assert.Single(comfy.Prompts.Skip(1));
    }

    [Fact]
    public async Task Two_prompts_arriving_together_start_only_one_render()
    {
        await using var comfy = await FakeComfy.StartAsync();
        comfy.PromptDelay = TimeSpan.FromMilliseconds(400);
        await using var runtime = Runtime(comfy);

        LocalReply[] replies = await Task.WhenAll(
            Send(runtime, "POST", "/prompt", Http.ImagePrompt()),
            Send(runtime, "POST", "/prompt", Http.ImagePrompt()));

        Assert.Single(replies, r => r.Status == 200);
        Assert.Single(replies, r => r.Status == 409);
        Assert.Single(comfy.Prompts);
    }

    [Fact]
    public async Task A_customer_prompt_ComfyUI_is_still_clearing_is_not_the_owners_queue()
    {
        await using var comfy = await FakeComfy.StartAsync();
        comfy.QueueAcceptedPrompts = true;
        await using var runtime = Runtime(comfy);

        string promptId = Http.Body(await Send(runtime, "POST", "/prompt", Http.ImagePrompt()))["prompt_id"]!.GetValue<string>();
        runtime.Consume(Http.Started(promptId));
        runtime.Consume(Http.Succeeded(promptId));
        Assert.True(comfy.Queue.ContainsKey(promptId));   // ComfyUI has not let go of it yet

        Assert.Equal(200, (await Send(runtime, "GET", "/aixman/ready")).Status);

        comfy.Queue["owners-batch"] = true;
        LocalReply ready = await Send(runtime, "GET", "/aixman/ready");
        Assert.Equal(503, ready.Status);
        Assert.Equal(ReadyStage.Busy, Http.Body(ready)["stage"]!.GetValue<string>());
        Assert.Contains("1 งาน", Http.Body(ready)["reason"]!.GetValue<string>());
        Assert.Equal(2, Http.Body(ready)["queue_remaining"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_render_still_executing_keeps_the_node_busy()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);
        runtime.Consume(Http.Started("rendering"));

        LocalReply ready = await Send(runtime, "GET", "/aixman/ready");
        LocalReply prompt = await Send(runtime, "POST", "/prompt", Http.ImagePrompt());

        Assert.Equal(503, ready.Status);
        Assert.Equal(ReadyStage.Busy, Http.Body(ready)["stage"]!.GetValue<string>());
        Assert.Equal(409, prompt.Status);
        Assert.False(comfy.Reached("POST /prompt"));
    }

    [Fact]
    public async Task Events_that_beat_the_submission_reply_are_replayed_after_it()
    {
        await using var comfy = await FakeComfy.StartAsync();
        comfy.PromptDelay = TimeSpan.FromMilliseconds(400);
        await using var runtime = Runtime(comfy);
        var events = new System.Collections.Concurrent.ConcurrentQueue<(JobStatus Status, bool Replayed)>();
        runtime.Job += e => events.Enqueue((e.Status, e.Replayed));

        Task<LocalReply> submitting = Send(runtime, "POST", "/prompt", Http.ImagePrompt());
        await Task.Delay(100);
        // A fully cached graph: ComfyUI starts and finishes it before its
        // answer to the submission has been read.
        runtime.Consume(Http.Started("prompt-1"));
        runtime.Consume(Http.Succeeded("prompt-1"));
        LocalReply reply = await submitting;

        Assert.Equal("prompt-1", Http.Body(reply)["prompt_id"]!.GetValue<string>());
        Assert.False(runtime.IsWorking);
        Assert.Equal(
            new[]
            {
                (JobStatus.Running, false), (JobStatus.Completed, false),
                (JobStatus.Queued, false), (JobStatus.Running, true), (JobStatus.Completed, true),
            },
            events.ToArray());
    }

    [Fact]
    public async Task A_prompt_is_rewritten_to_report_progress_to_the_node()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);

        await Send(runtime, "POST", "/prompt", Http.ImagePrompt());

        JsonNode forwarded = JsonNode.Parse(comfy.Prompts.Single())!;
        Assert.NotEqual("aixman", forwarded["client_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Readiness_lists_only_the_kinds_the_owner_offers()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy, report: Report("image", "video", "audio"));
        runtime.OffersKind = kind => kind != "video";

        LocalReply ready = await Send(runtime, "GET", "/aixman/ready");

        Assert.Equal(200, ready.Status);
        JsonNode assessment = Http.Body(ready)["assessment"]!;
        string[] canRun = assessment["can_run"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "image", "audio" }, canRun);
        Assert.Null(assessment["lanes"]!["video"]);
        Assert.NotNull(runtime.CurrentGpuHash);
    }

    // ---------------------------------------------------------- stuck state

    [Fact]
    public async Task Settling_a_prompt_the_socket_never_finished_clears_the_busy_flag()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);
        runtime.TrackTunnelPrompt("p1");
        runtime.Consume(Http.Started("p1"));
        Assert.True(runtime.IsBusy);
        Assert.True(runtime.IsWorking);

        Assert.True(runtime.Settle("p1", success: false));

        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsWorking);
        Assert.Empty(runtime.TrackedPrompts());
    }

    [Fact]
    public async Task History_tells_absent_from_unreachable()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);
        comfy.Finished("done-1", "aix_00001_.png");

        Assert.Equal(HistoryState.Done, (await runtime.HistoryAsync("done-1", CancellationToken.None)).State);
        Assert.Equal(HistoryState.Absent, (await runtime.HistoryAsync("never", CancellationToken.None)).State);

        await using var offline = new ComfyRuntime(new NodeOptions { ComfyUrl = "http://127.0.0.1:9" }, new NullLog());
        Assert.Equal(HistoryState.Unknown, (await offline.HistoryAsync("done-1", CancellationToken.None)).State);
        Assert.Null(await offline.ReadQueueAsync(CancellationToken.None));
    }

    // ------------------------------------------------------------------ purge

    private static async Task<string> SubmitAndFinishAsync(ComfyRuntime runtime, FakeComfy comfy, string outputName, string? loadImage = null)
    {
        LocalReply reply = await Send(runtime, "POST", "/prompt", Http.ImagePrompt(loadImage));
        string promptId = Http.Body(reply)["prompt_id"]!.GetValue<string>();
        runtime.Consume(Http.Started(promptId));
        runtime.Consume(Http.Succeeded(promptId));
        comfy.Finished(promptId, outputName);
        return promptId;
    }

    [Fact]
    public async Task Purge_deletes_the_jobs_output_and_its_history_and_is_idempotent()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy, options: new NodeOptions { ComfyUrl = comfy.Url, ComfyBaseDirectory = dir.Path });
        string output = dir.File("output/aix_00001_.png");
        string ownersOwn = dir.File("output/owners_render.png");
        string promptId = await SubmitAndFinishAsync(runtime, comfy, "aix_00001_.png");

        LocalReply first = await Send(runtime, "POST", "/aixman/purge", Http.Json(new { prompt_id = promptId }));

        Assert.Equal(200, first.Status);
        Assert.Equal(1, Http.Body(first)["purged"]!.GetValue<int>());
        Assert.True(Http.Body(first)["history"]!.GetValue<bool>());
        Assert.False(File.Exists(output));
        Assert.True(File.Exists(ownersOwn));
        Assert.Contains(promptId, comfy.HistoryDeletes);

        // Again, from a retry: nothing left, still a success.
        LocalReply again = await Send(runtime, "POST", "/aixman/purge", Http.Json(new { prompt_id = promptId }));
        Assert.Equal(200, again.Status);
        Assert.Equal(0, Http.Body(again)["purged"]!.GetValue<int>());
    }

    [Fact]
    public async Task Purge_never_touches_a_prompt_that_did_not_come_down_the_tunnel()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy, options: new NodeOptions { ComfyUrl = comfy.Url, ComfyBaseDirectory = dir.Path });
        string ownersOwn = dir.File("output/owners_render.png");
        comfy.Finished("owners-prompt", "owners_render.png");

        LocalReply reply = await Send(runtime, "POST", "/aixman/purge", Http.Json(new { prompt_id = "owners-prompt" }));

        Assert.Equal(200, reply.Status);
        Assert.Equal(0, Http.Body(reply)["purged"]!.GetValue<int>());
        Assert.True(File.Exists(ownersOwn));
        Assert.Empty(comfy.HistoryDeletes);
        Assert.True(comfy.History.ContainsKey("owners-prompt"));
    }

    [Fact]
    public async Task Purge_knows_tunnel_prompts_from_the_ledger_after_a_restart()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        using var store = new Storage.NodeStore(Path.Combine(dir.Path, "node.db"));
        store.JobSubmitted("from-last-run", "image", 4, freeShare: false);
        store.JobFinished("from-last-run", true, "aix_00009_.png", null);
        string output = dir.File("comfy/output/aix_00009_.png");
        comfy.Finished("from-last-run", "aix_00009_.png");

        await using var runtime = Runtime(comfy, options: new NodeOptions { ComfyUrl = comfy.Url, ComfyBaseDirectory = Path.Combine(dir.Path, "comfy") });
        runtime.Ledger = store;

        PurgeResult result = await runtime.PurgeAsync("from-last-run", CancellationToken.None);

        Assert.Equal(1, result.Files);
        Assert.False(File.Exists(output));
        Assert.Empty(store.JobsToPurge(DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddDays(-1)));
    }

    [Fact]
    public async Task Purge_closes_a_finished_prompt_the_socket_never_reported()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy, options: new NodeOptions { ComfyUrl = comfy.Url, ComfyBaseDirectory = dir.Path });
        string output = dir.File("output/aix_00005_.png");
        var ended = new List<ComfyRuntime.JobEvent>();
        runtime.Job += e => { if (e.Status is JobStatus.Completed or JobStatus.Failed) ended.Add(e); };

        string promptId = Http.Body(await Send(runtime, "POST", "/prompt", Http.ImagePrompt()))["prompt_id"]!.GetValue<string>();
        comfy.Finished(promptId, "aix_00005_.png");   // done, and the socket said nothing
        Assert.True(runtime.HasTunnelWork);
        Assert.False(runtime.CollectedSinceLastFinish);

        LocalReply reply = await Send(runtime, "POST", "/aixman/purge", Http.Json(new { prompt_id = promptId }));

        Assert.Equal(200, reply.Status);
        Assert.False(File.Exists(output));
        Assert.False(runtime.HasTunnelWork);
        Assert.True(runtime.CollectedSinceLastFinish);
        var completed = Assert.Single(ended);
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal("aix_00005_.png", completed.Filename);
    }

    [Fact]
    public async Task Purge_of_a_render_still_running_touches_nothing()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);
        LocalReply submitted = await Send(runtime, "POST", "/prompt", Http.ImagePrompt());
        string promptId = Http.Body(submitted)["prompt_id"]!.GetValue<string>();

        LocalReply reply = await Send(runtime, "POST", "/aixman/purge", Http.Json(new { prompt_id = promptId }));

        // "Not yet", in a status aixman asks again after — not a 409, which it
        // reads as a refusal that will never change.
        Assert.Equal(503, reply.Status);
        Assert.Equal("prompt-running", Http.Body(reply)["error"]!.GetValue<string>());
        Assert.Null(Http.Body(reply)["stage"]);
        Assert.Empty(comfy.HistoryDeletes);
    }

    [Fact]
    public async Task Purge_refuses_a_file_name_that_walks_out_of_the_folder()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy, options: new NodeOptions { ComfyUrl = comfy.Url, ComfyBaseDirectory = Path.Combine(dir.Path, "comfy") });
        dir.File("comfy/output/keep.txt");
        string outside = dir.File("precious.txt");
        LocalReply submitted = await Send(runtime, "POST", "/prompt", Http.ImagePrompt());
        string promptId = Http.Body(submitted)["prompt_id"]!.GetValue<string>();
        runtime.Consume(Http.Started(promptId));
        runtime.Consume(Http.Succeeded(promptId));
        comfy.Finished(promptId, "precious.txt", subfolder: "../..");

        PurgeResult result = await runtime.PurgeAsync(promptId, CancellationToken.None);

        Assert.Equal(0, result.Files);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Purge_takes_the_customers_upload_with_the_job()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy, options: new NodeOptions { ComfyUrl = comfy.Url, ComfyBaseDirectory = dir.Path });
        dir.File("output/aix_00002_.png");
        string upload = dir.File("input/customer-face.png");

        ComfyRuntime.JobEvent? queued = null;
        runtime.Job += e => { if (e.Status == JobStatus.Queued) queued = e; };

        Assert.Equal(200, (await Send(runtime, "POST", "/upload/image", [1, 2, 3])).Status);
        string promptId = await SubmitAndFinishAsync(runtime, comfy, "aix_00002_.png", loadImage: "customer-face.png");

        Assert.NotNull(queued);
        Assert.Single(queued!.Inputs);

        PurgeResult result = await runtime.PurgeAsync(promptId, CancellationToken.None);

        Assert.Equal(2, result.Files);
        Assert.False(File.Exists(upload));
    }

    [Fact]
    public async Task Purge_finds_ComfyUIs_folders_from_ComfyUI_itself()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        comfy.CustomNodesPath = Path.Combine(dir.Path, "ComfyUI", "custom_nodes");
        Directory.CreateDirectory(Path.Combine(dir.Path, "ComfyUI", "output"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "elsewhere", "input"));
        comfy.Argv = ["main.py", "--input-directory", Path.Combine(dir.Path, "elsewhere", "input")];
        await using var runtime = Runtime(comfy);

        ComfyFolders folders = await runtime.FoldersAsync(CancellationToken.None);

        Assert.Equal(Path.Combine(dir.Path, "ComfyUI", "output"), folders.Output);
        Assert.Equal(Path.Combine(dir.Path, "elsewhere", "input"), folders.Input);
        Assert.Null(folders.Temp);   // not on disk, so not guessed
    }

    [Fact]
    public async Task Purge_wants_POST_and_a_prompt_id()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);

        Assert.Equal(405, (await Send(runtime, "GET", "/aixman/purge")).Status);
        Assert.Equal(400, (await Send(runtime, "POST", "/aixman/purge", Http.Json(new { }))).Status);
        Assert.Equal(400, (await Send(runtime, "POST", "/aixman/purge", Http.Json(new { prompt_id = "../../x" }))).Status);
    }

    [Theory]
    [InlineData("", "a.png", true)]
    [InlineData("sub/dir", "a.png", true)]
    [InlineData("..", "a.png", false)]
    [InlineData("", "../a.png", false)]
    [InlineData("", "a/../../b.png", false)]
    [InlineData("C:\\Windows", "a.png", false)]
    [InlineData("/etc", "passwd", false)]
    [InlineData("", "a:b.png", false)]
    public void ResolveInside_keeps_every_name_inside_its_folder(string subfolder, string filename, bool allowed)
    {
        using var dir = new TempDir();
        string? path = ComfyRuntime.ResolveInside(dir.Path, subfolder, filename);
        Assert.Equal(allowed, path is not null);
        if (path is not null) Assert.StartsWith(dir.Path, path, StringComparison.OrdinalIgnoreCase);
    }
}
