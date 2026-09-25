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
        Assert.True(comfy.Reached("GET /object_info"));

        // A job's own history, then its own file — the order aixman reads them in.
        string promptId = await SubmitAndFinishAsync(runtime, comfy, "aix_00001_.png");
        LocalReply history = await Send(runtime, "GET", $"/history/{promptId}");
        Assert.Equal(200, history.Status);
        Assert.NotNull(Http.Body(history)[promptId]);
        Assert.Equal(200, (await Send(runtime, "GET", "/view?filename=aix_00001_.png&subfolder=&type=output")).Status);
        Assert.True(comfy.Reached("GET /view"));
    }

    // ---------------------------------------------------------------- scope

    [Theory]
    [InlineData("/view?filename=ComfyUI_00001_.png&type=output")]
    [InlineData("/view?filename=owners_face.png&type=input")]
    [InlineData("/view?filename=aix_00001_.png&subfolder=other&type=output")]
    public async Task A_file_no_customer_job_named_is_not_found_and_never_read(string path)
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);
        string promptId = await SubmitAndFinishAsync(runtime, comfy, "aix_00001_.png");
        await Send(runtime, "GET", $"/history/{promptId}");
        comfy.Finished("owners-prompt", "ComfyUI_00001_.png");

        LocalReply reply = await Send(runtime, "GET", path);

        Assert.Equal(404, reply.Status);
        Assert.False(comfy.Reached("GET /view"));
    }

    [Fact]
    public async Task A_jobs_file_is_found_again_after_a_restart_between_history_and_download()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        using var store = new Storage.NodeStore(Path.Combine(dir.Path, "node.db"));
        store.JobSubmitted("from-last-run", "image", 4, freeShare: false);
        store.JobFinished("from-last-run", true, "aix_00031_.png", null);
        comfy.Finished("from-last-run", "aix_00031_.png");
        await using var runtime = Runtime(comfy);
        runtime.Ledger = store;

        Assert.Equal(200, (await Send(runtime, "GET", "/view?filename=aix_00031_.png&type=output")).Status);
    }

    [Fact]
    public async Task The_owners_prompts_are_not_readable_through_the_tunnel()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);
        comfy.Finished("owners-prompt", "owners_render.png");

        LocalReply list = await Send(runtime, "GET", "/history?max_items=1000");
        LocalReply one = await Send(runtime, "GET", "/history/owners-prompt");

        Assert.Equal(403, list.Status);
        Assert.Equal(200, one.Status);
        Assert.Equal("{}", Http.Body(one).ToJsonString());
        Assert.False(comfy.Reached("GET /history"));
    }

    [Fact]
    public async Task The_queue_shows_the_tunnels_prompts_and_none_of_the_owners()
    {
        await using var comfy = await FakeComfy.StartAsync();
        comfy.QueueAcceptedPrompts = true;
        await using var runtime = Runtime(comfy);
        string promptId = Http.Body(await Send(runtime, "POST", "/prompt", Http.ImagePrompt()))["prompt_id"]!.GetValue<string>();
        comfy.Queue["owners-batch"] = true;

        LocalReply reply = await Send(runtime, "GET", "/queue");

        Assert.Equal(200, reply.Status);
        string text = Http.Body(reply).ToJsonString();
        Assert.Contains(promptId, text);
        Assert.DoesNotContain("owners-batch", text);

        // The node's own count, which the gate reads, still sees both.
        Assert.Equal(2, (await runtime.ReadQueueAsync(CancellationToken.None))!.Count);
    }

    [Fact]
    public async Task Deleting_history_deletes_only_the_tunnels_own_prompts()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);
        string promptId = await SubmitAndFinishAsync(runtime, comfy, "aix_00003_.png");
        comfy.Finished("owners-prompt", "owners_render.png");

        LocalReply reply = await Send(runtime, "POST", "/history", Http.Json(new { delete = new[] { "owners-prompt", promptId } }));
        LocalReply onlyOwners = await Send(runtime, "POST", "/history", Http.Json(new { delete = new[] { "owners-prompt" } }));

        Assert.Equal(200, reply.Status);
        Assert.Equal(200, onlyOwners.Status);
        Assert.Equal(new[] { promptId }, comfy.HistoryDeletes.ToArray());
        Assert.True(comfy.History.ContainsKey("owners-prompt"));
    }

    [Fact]
    public async Task Interrupt_stops_a_customer_render_by_name_and_leaves_the_owners_alone()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);

        runtime.Consume(Http.Started("owners-render"));
        Assert.Equal(200, (await Send(runtime, "POST", "/interrupt")).Status);
        Assert.Empty(comfy.Interrupts);

        runtime.Consume(Http.Succeeded("owners-render"));
        string promptId = Http.Body(await Send(runtime, "POST", "/prompt", Http.ImagePrompt()))["prompt_id"]!.GetValue<string>();
        runtime.Consume(Http.Started(promptId));

        Assert.Equal(200, (await Send(runtime, "POST", "/interrupt", Http.Json(new { prompt_id = "owners-render" }))).Status);
        Assert.Empty(comfy.Interrupts);

        Assert.Equal(200, (await Send(runtime, "POST", "/interrupt")).Status);
        string sent = Assert.Single(comfy.Interrupts);
        Assert.Equal(promptId, JsonNode.Parse(sent)!["prompt_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("aixman-first-1.png", null, null, true)]
    [InlineData("aixman-source-2.mp3", "input", "", true)]
    [InlineData("ComfyUI_00001_.png", null, null, false)]      // the owner's own render, by name
    [InlineData("aixman-first-1.png", "output", null, false)]  // into the output folder
    [InlineData("aixman-first-1.png", "temp", null, false)]
    [InlineData("aixman-first-1.png", null, "nested", false)]
    public async Task Uploads_land_only_as_aixman_files_at_the_top_of_the_input_folder(string filename, string? type, string? subfolder, bool allowed)
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);
        var (headers, body) = Http.Upload(filename, type, subfolder);

        LocalReply reply = await runtime.HandleAsync("POST", "/upload/image", headers, body, CancellationToken.None);

        Assert.Equal(allowed ? 200 : 403, reply.Status);
        Assert.Equal(allowed, comfy.Reached("POST /upload/image"));
    }

    [Fact]
    public async Task An_upload_is_read_whatever_the_case_of_its_content_type_header()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);
        var (headers, body) = Http.Upload("aixman-first-1.png");
        // Off the wire the map is case-sensitive, and a caller may send it lower-case.
        var lower = new Dictionary<string, string> { ["content-type"] = headers["Content-Type"] };

        LocalReply reply = await runtime.HandleAsync("POST", "/upload/image", lower, body, CancellationToken.None);

        Assert.Equal(200, reply.Status);
        Assert.Equal("aixman-first-1.png", Http.Body(reply)["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"image\"; filename=\"aixman-a.png\"\r\n\r\nx\r\n")]   // never closed
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"image\"; filename=\"aixman-a.png\"\r\nx\r\n--b--\r\n")] // headers never end
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"image\"; filename=\"aixman-a.png\"\r\n\r\nx\r\n--b\r\nContent-Disposition: form-data; name=\"image\"; filename=\"aixman-b.png\"\r\n\r\ny\r\n--b--\r\n")]
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"image\"; filename=\"aixman-../x.png\"\r\n\r\nx\r\n--b--\r\n")]
    [InlineData("nothing that looks like a form")]
    public void A_malformed_or_doubled_upload_is_refused(string form)
    {
        var headers = new Dictionary<string, string> { ["Content-Type"] = "multipart/form-data; boundary=b" };

        Assert.NotNull(ComfyRuntime.UploadRefusal(headers, System.Text.Encoding.UTF8.GetBytes(form)));
    }

    [Fact]
    public async Task An_upload_that_is_not_a_form_is_refused()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);

        LocalReply reply = await Send(runtime, "POST", "/upload/image", [1, 2, 3]);

        Assert.Equal(403, reply.Status);
        Assert.Equal("upload-not-allowed", Http.Body(reply)["error"]!.GetValue<string>());
        Assert.False(comfy.Reached("POST /upload/image"));
    }

    // ------------------------------------------------------------ prompt ids

    [Fact]
    public async Task A_submission_cannot_choose_its_prompt_id_or_its_place_in_the_queue()
    {
        await using var comfy = await FakeComfy.StartAsync();
        comfy.HonorPromptId = true;
        await using var runtime = Runtime(comfy);
        comfy.Finished("0f8c4f7e-3b1a-4c2e-9d3b-6b1f0a2c9e11", "owners_render.png");

        var submission = JsonNode.Parse(Http.ImagePrompt())!.AsObject();
        submission["prompt_id"] = "0f8c4f7e-3b1a-4c2e-9d3b-6b1f0a2c9e11";
        submission["front"] = true;
        submission["number"] = -1000;
        LocalReply reply = await Send(runtime, "POST", "/prompt", Http.Json(submission));

        JsonNode forwarded = JsonNode.Parse(comfy.Prompts.Single())!;
        string chosen = forwarded["prompt_id"]!.GetValue<string>();
        Assert.NotEqual("0f8c4f7e-3b1a-4c2e-9d3b-6b1f0a2c9e11", chosen);
        Assert.True(Guid.TryParse(chosen, out _));
        Assert.Null(forwarded["front"]);
        Assert.Null(forwarded["number"]);
        Assert.Equal(chosen, Http.Body(reply)["prompt_id"]!.GetValue<string>());

        // So the owner's prompt is not "known", and a purge touches nothing of it.
        LocalReply purge = await Send(runtime, "POST", "/aixman/purge", Http.Json(new { prompt_id = "0f8c4f7e-3b1a-4c2e-9d3b-6b1f0a2c9e11" }));
        Assert.Equal(0, Http.Body(purge)["purged"]!.GetValue<int>());
        Assert.Empty(comfy.HistoryDeletes);
    }

    [Fact]
    public async Task A_body_that_is_not_a_prompt_object_is_refused_before_ComfyUI()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy);

        LocalReply reply = await Send(runtime, "POST", "/prompt", "[1,2]"u8.ToArray());

        Assert.Equal(400, reply.Status);
        Assert.False(comfy.Reached("POST /prompt"));
        Assert.False(runtime.HasTunnelWork);
    }

    [Fact]
    public async Task A_prompt_whose_caller_gave_up_is_tracked_all_the_same()
    {
        await using var comfy = await FakeComfy.StartAsync();
        comfy.PromptDelay = TimeSpan.FromMilliseconds(500);
        await using var runtime = Runtime(comfy);
        var queued = new List<string>();
        runtime.Job += e => { if (e.Status == JobStatus.Queued) queued.Add(e.PromptId); };

        // The relay cancels the request a moment after it arrives: aixman's
        // submit timed out, or the socket blipped.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        LocalReply reply = await runtime.HandleAsync("POST", "/prompt", Http.NoHeaders, Http.ImagePrompt(), cancel.Token);

        Assert.Equal(200, reply.Status);
        Assert.True(runtime.HasTunnelWork);
        Assert.Equal("prompt-1", Assert.Single(queued));
    }

    [Fact]
    public async Task A_prompt_ComfyUI_queued_without_answering_is_tracked_under_the_nodes_id()
    {
        await using var comfy = await FakeComfy.StartAsync();
        comfy.HonorPromptId = true;
        comfy.QueueAcceptedPrompts = true;
        comfy.DropPromptAnswers = true;
        await using var runtime = Runtime(comfy);

        LocalReply reply = await Send(runtime, "POST", "/prompt", Http.ImagePrompt());

        Assert.Equal(200, reply.Status);
        string promptId = Http.Body(reply)["prompt_id"]!.GetValue<string>();
        Assert.True(comfy.Queue.ContainsKey(promptId));
        Assert.True(runtime.HasTunnelWork);
        Assert.Contains(promptId, runtime.TrackedPrompts());
    }

    [Fact]
    public async Task A_prompt_ComfyUI_dropped_without_queueing_is_refused_with_a_stage()
    {
        await using var comfy = await FakeComfy.StartAsync();
        comfy.HonorPromptId = true;
        comfy.DropPromptAnswers = true;
        await using var runtime = Runtime(comfy);

        LocalReply reply = await Send(runtime, "POST", "/prompt", Http.ImagePrompt());

        Assert.Equal(503, reply.Status);
        Assert.Equal(ReadyStage.Paused, Http.Body(reply)["stage"]!.GetValue<string>());
        Assert.False(runtime.HasTunnelWork);
    }

    [Fact]
    public async Task While_ComfyUI_is_down_new_work_is_refused_with_a_stage_not_a_502()
    {
        await using var runtime = new ComfyRuntime(new NodeOptions { ComfyUrl = "http://127.0.0.1:9" }, new NullLog());
        runtime.AssessmentSource = () => (Report("image"), null);
        var (headers, body) = Http.Upload("aixman-first-1.png");

        LocalReply ready = await Send(runtime, "GET", "/aixman/ready");
        LocalReply prompt = await Send(runtime, "POST", "/prompt", Http.ImagePrompt());
        LocalReply upload = await runtime.HandleAsync("POST", "/upload/image", headers, body, CancellationToken.None);

        foreach (LocalReply reply in new[] { ready, prompt, upload })
        {
            Assert.Equal(503, reply.Status);
            Assert.Equal(ReadyStage.Paused, Http.Body(reply)["stage"]!.GetValue<string>());
            Assert.Contains("ComfyUI", Http.Body(reply)["reason"]!.GetValue<string>());
        }
        Assert.False(runtime.HasTunnelWork);
    }

    // aixman reads the node list first on every submission once its copy is
    // ten minutes old. A 502 there is a failed attempt and a machine to avoid;
    // a stage is "not now", and the job goes back in the queue whole.
    [Theory]
    [InlineData("/object_info")]
    [InlineData("/object_info/KSampler")]
    public async Task While_ComfyUI_is_down_the_node_list_is_refused_with_a_stage_not_a_502(string path)
    {
        await using var runtime = new ComfyRuntime(new NodeOptions { ComfyUrl = "http://127.0.0.1:9" }, new NullLog());
        runtime.AssessmentSource = () => (Report("image"), null);

        LocalReply reply = await Send(runtime, "GET", path);

        Assert.Equal(503, reply.Status);
        JsonNode body = Http.Body(reply);
        Assert.False(body["ready"]!.GetValue<bool>());
        Assert.Equal(ReadyStage.Paused, body["stage"]!.GetValue<string>());
        Assert.Contains("ComfyUI", body["reason"]!.GetValue<string>());
        Assert.Null(body["error"]);
    }

    // Only a ComfyUI that cannot be reached is turned into a stage. The list
    // itself takes no work, so a node the owner has paused still answers it,
    // and the prompt behind it is the one the gate refuses.
    [Fact]
    public async Task A_paused_node_still_answers_the_node_list()
    {
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy, () => new AcceptDecision(false, "เจ้าของกำลังใช้เครื่อง", ReadyStage.Paused));

        LocalReply list = await Send(runtime, "GET", "/object_info");
        LocalReply prompt = await Send(runtime, "POST", "/prompt", Http.ImagePrompt());

        Assert.Equal(200, list.Status);
        Assert.True(comfy.Reached("GET /object_info"));
        Assert.Equal(503, prompt.Status);
        Assert.Equal(ReadyStage.Paused, Http.Body(prompt)["stage"]!.GetValue<string>());
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

        ComfyRuntime.JobEvent? queued = null;
        runtime.Job += e => { if (e.Status == JobStatus.Queued) queued = e; };

        var (headers, body) = Http.Upload("aixman-first-face.png");
        string upload = dir.File("input/aixman-first-face.png");
        Assert.Equal(200, (await runtime.HandleAsync("POST", "/upload/image", headers, body, CancellationToken.None)).Status);
        string promptId = await SubmitAndFinishAsync(runtime, comfy, "aix_00002_.png", loadImage: "aixman-first-face.png");

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
    public async Task Purge_reads_ComfyUIs_folders_again_once_they_may_have_changed()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        comfy.CustomNodesPath = Path.Combine(dir.Path, "A", "custom_nodes");
        Directory.CreateDirectory(Path.Combine(dir.Path, "A", "output"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "B", "output"));
        await using var runtime = Runtime(comfy);
        runtime.FoldersTrustedFor = TimeSpan.Zero;

        Assert.Equal(Path.Combine(dir.Path, "A", "output"), (await runtime.FoldersAsync(CancellationToken.None)).Output);

        // The owner closed install A and started B on the same port.
        comfy.CustomNodesPath = Path.Combine(dir.Path, "B", "custom_nodes");
        Assert.Equal(Path.Combine(dir.Path, "B", "output"), (await runtime.FoldersAsync(CancellationToken.None)).Output);
    }

    [Fact]
    public async Task Purge_leaves_an_older_file_under_the_same_name_alone()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var runtime = Runtime(comfy, options: new NodeOptions { ComfyUrl = comfy.Url, ComfyBaseDirectory = dir.Path });
        // The owner's render from before the job, with the name a counter
        // gives out again — in another install, or pointed at by a cached prompt.
        string owners = dir.File("output/ComfyUI_00012_.png");
        File.SetLastWriteTimeUtc(owners, DateTime.UtcNow.AddHours(-3));

        string promptId = await SubmitAndFinishAsync(runtime, comfy, "ComfyUI_00012_.png");
        PurgeResult result = await runtime.PurgeAsync(promptId, CancellationToken.None);

        Assert.Equal(0, result.Files);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(owners));
        Assert.True(result.HistoryDeleted);
    }

    [Fact]
    public async Task Purge_touches_nothing_while_the_id_is_still_in_the_queue()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        using var store = new Storage.NodeStore(Path.Combine(dir.Path, "node.db"));
        store.JobSubmitted("reused-id", "image", 4, freeShare: false);
        string output = dir.File("comfy/output/aix_00020_.png");
        comfy.Finished("reused-id", "aix_00020_.png");
        comfy.Queue["reused-id"] = true;   // a second submission under the same id, not yet run
        await using var runtime = Runtime(comfy, options: new NodeOptions { ComfyUrl = comfy.Url, ComfyBaseDirectory = Path.Combine(dir.Path, "comfy") });
        runtime.Ledger = store;

        PurgeResult result = await runtime.PurgeAsync("reused-id", CancellationToken.None);

        Assert.True(result.Running);
        Assert.True(File.Exists(output));
        Assert.Empty(comfy.HistoryDeletes);
    }

    [Fact]
    public async Task Only_aixman_purging_the_job_that_just_ended_counts_as_collected()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        using var store = new Storage.NodeStore(Path.Combine(dir.Path, "node.db"));
        await using var runtime = Runtime(comfy);
        runtime.Ledger = store;
        // What the host does with every accepted job.
        runtime.Job += e => { if (e.Status == JobStatus.Queued) store.JobSubmitted(e.PromptId, e.Kind, e.NodesTotal, freeShare: false); };
        store.JobSubmitted("six-hours-old", "image", 4, freeShare: false);
        store.JobFinished("six-hours-old", true, "aix_00001_.png", null);
        comfy.Finished("six-hours-old", "aix_00001_.png");

        string justNow = await SubmitAndFinishAsync(runtime, comfy, "aix_00002_.png");
        Assert.False(runtime.CollectedSinceLastFinish);

        // The node's own fallback clearing an old job, seconds after the render.
        await runtime.PurgeAsync("six-hours-old", CancellationToken.None);
        Assert.False(runtime.CollectedSinceLastFinish);

        // aixman retrying the purge of an earlier job.
        Assert.Equal(200, (await Send(runtime, "POST", "/aixman/purge", Http.Json(new { prompt_id = "six-hours-old" }))).Status);
        Assert.False(runtime.CollectedSinceLastFinish);

        // The fallback reaching the new job itself is still not aixman.
        await runtime.PurgeAsync(justNow, CancellationToken.None);
        Assert.False(runtime.CollectedSinceLastFinish);

        Assert.Equal(200, (await Send(runtime, "POST", "/aixman/purge", Http.Json(new { prompt_id = justNow }))).Status);
        Assert.True(runtime.CollectedSinceLastFinish);
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
