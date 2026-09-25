using System.Text.Json;
using GpuxMine.Core.Updates;
using GpuxMine.Node.Assessment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GpuxMine.Node.Tests;

/// <summary>
/// The node end to end, minus the relay: the remembered switch, the drain,
/// the gate the owner's settings put on new work, the heartbeat, and the
/// ledger reconciler. The relay URL points at a closed port, so a session
/// "runs" without ever connecting.
/// </summary>
public class NodeHostTests
{
    private const string ClosedRelay = "ws://127.0.0.1:9/agent";

    private static NodeOptions Paired(TempDir dir, string comfyUrl = "http://127.0.0.1:9") => new()
    {
        DataDirectory = dir.Path,
        WorkerId = "gxm-test",
        Token = "agent-token",
        RelayUrl = ClosedRelay,
        ComfyUrl = comfyUrl,
        XmanStudioUrl = "http://127.0.0.1:9",
        AutoUpdate = false,
    };

    private static NodeAssessment UsableReport(params string[] kinds) => new()
    {
        AgentVersion = SelfUpdater.CurrentVersion,
        MeasuredAt = DateTimeOffset.UtcNow,
        GpuName = "cuda:0 NVIDIA GeForce RTX 3060 : cudaMallocAsync",
        VramTotalMb = 12288,
        GpuHash = NodeAssessment.GpuHashOf("cuda:0 NVIDIA GeForce RTX 3060 : cudaMallocAsync", 12288),
        Score = 800,
        Tier = "gold",
        Capabilities = kinds.Select(k => new Capability { Kind = k, CanRun = true, Lane = "full", SecondsPerUnit = 10 }).ToList(),
    };

    /// <summary>A paired, assessed node that is sharing, in front of a stand-in ComfyUI whose folders live in <paramref name="dir"/>.</summary>
    private static async Task<NodeHost> SharingHost(TempDir dir, FakeComfy comfy)
    {
        var host = new NodeHost(Paired(dir, comfy.Url) with { ComfyBaseDirectory = Path.Combine(dir.Path, "comfy") });
        host.UseAssessment(UsableReport("image"));
        await host.OwnerStartAsync();
        return host;
    }

    private static Task<LocalReply> Tunnel(NodeHost host, string method, string path, byte[]? body = null) =>
        host.Runtime.HandleAsync(method, path, Http.NoHeaders, body ?? [], CancellationToken.None);

    private static JobRecord Job(NodeHost host, string promptId) =>
        host.Store.RecentJobs(100).Single(j => j.PromptId == promptId);

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("the node did not get there in time");
            await Task.Delay(50);
        }
    }

    // -------------------------------------------------------- the switch

    [Fact]
    public async Task START_and_STOP_are_remembered_across_launches()
    {
        using var dir = new TempDir();

        await using (var host = new NodeHost(Paired(dir)))
        {
            await host.OwnerStartAsync();
            Assert.True(host.State.Running);
            Assert.True(host.Settings.SharingEnabled);
        }

        await using (var host = new NodeHost(Paired(dir)))
        {
            Assert.True(host.ShouldResumeSharing);
            Assert.True(host.ResumeSharingIfEnabled());
            Assert.True(host.State.Running);
            await host.OwnerStopAsync(now: true);
            Assert.False(host.State.Running);
        }

        await using (var host = new NodeHost(Paired(dir)))
        {
            Assert.False(host.Settings.SharingEnabled);
            Assert.False(host.ShouldResumeSharing);
            Assert.False(host.ResumeSharingIfEnabled());
            Assert.False(host.State.Running);
        }
    }

    [Fact]
    public async Task A_node_from_before_the_switch_existed_resumes_and_then_remembers()
    {
        using var dir = new TempDir();
        await using (var host = new NodeHost(Paired(dir)))
        {
            Assert.Null(host.Settings.SharingEnabled);
            Assert.True(host.ResumeSharingIfEnabled());
            Assert.True(host.Settings.SharingEnabled);
        }

        using var store = new Storage.NodeStore(Path.Combine(dir.Path, "node.db"));
        Assert.True(NodeSettings.Load(store).SharingEnabled);
    }

    [Fact]
    public async Task Shutting_down_is_not_the_owner_pressing_STOP()
    {
        using var dir = new TempDir();
        await using (var host = new NodeHost(Paired(dir)))
        {
            await host.OwnerStartAsync();
        }   // disposed while sharing: an update, a quit, a reboot

        await using var again = new NodeHost(Paired(dir));
        Assert.True(again.ShouldResumeSharing);
    }

    [Fact]
    public async Task An_unpaired_node_never_starts_a_session()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(new NodeOptions { DataDirectory = dir.Path, RelayUrl = ClosedRelay, AutoUpdate = false });

        Assert.False(host.ShouldResumeSharing);
        await host.StartAsync();

        Assert.False(host.State.Running);
        Assert.Equal(ConnectionState.Stopped, host.State.Connection);
    }

    // ----------------------------------------------------------- the drain

    [Fact]
    public async Task STOP_during_a_customer_render_keeps_the_session_until_it_is_handed_over()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));
        host.DrainGrace = TimeSpan.FromMilliseconds(300);
        await host.OwnerStartAsync();
        host.Runtime.TrackTunnelPrompt("p1");
        host.Runtime.Consume(Http.Started("p1"));

        Task stopping = host.OwnerStopAsync();

        Assert.True(host.State.Running);
        Assert.True(host.State.Draining);
        Assert.False(host.Settings.SharingEnabled);
        AcceptDecision decision = host.Decide();
        Assert.False(decision.Accept);
        Assert.Equal(ReadyStage.Draining, decision.Stage);

        // Still waiting while the render runs.
        await Task.Delay(600);
        Assert.True(host.State.Running);

        host.Runtime.Consume(Http.Succeeded("p1"));
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(host.State.Running);
        Assert.False(host.State.Draining);
        Assert.Equal(ConnectionState.Stopped, host.State.Connection);
    }

    [Fact]
    public async Task STOP_right_after_a_render_waits_for_aixman_to_collect_it()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));
        host.DrainGrace = TimeSpan.FromMilliseconds(800);
        await host.OwnerStartAsync();
        host.Runtime.TrackTunnelPrompt("p1");
        host.Runtime.Consume(Http.Started("p1"));
        host.Runtime.Consume(Http.Succeeded("p1"));

        Task stopping = host.OwnerStopAsync();
        await Task.Delay(200);
        Assert.True(host.State.Running);
        Assert.Contains("รอ pool เก็บผลงาน", host.State.DrainNote);

        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(host.State.Running);
    }

    [Fact]
    public async Task STOP_when_idle_stops_at_once()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));
        await host.OwnerStartAsync();

        await host.OwnerStopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(host.State.Running);
    }

    [Fact]
    public async Task START_during_a_stop_cancels_it_and_keeps_the_session()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));
        host.DrainGrace = TimeSpan.FromMilliseconds(200);
        await host.OwnerStartAsync();
        host.Runtime.TrackTunnelPrompt("p1");
        host.Runtime.Consume(Http.Started("p1"));

        Task stopping = host.OwnerStopAsync();
        await host.OwnerStartAsync();

        Assert.False(host.State.Draining);
        Assert.True(host.Settings.SharingEnabled);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        host.Runtime.Consume(Http.Succeeded("p1"));
        await Task.Delay(800);
        Assert.True(host.State.Running);
        Assert.False(host.State.Draining);
    }

    [Fact]
    public async Task A_stop_ends_as_soon_as_aixman_has_purged_the_result()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var host = await SharingHost(dir, comfy);
        host.DrainGrace = TimeSpan.FromMinutes(10);
        dir.File("comfy/output/aix_00008_.png");

        string promptId = Http.Body(await Tunnel(host, "POST", "/prompt", Http.ImagePrompt()))["prompt_id"]!.GetValue<string>();
        host.Runtime.Consume(Http.Started(promptId));
        host.Runtime.Consume(Http.Succeeded(promptId));
        comfy.Finished(promptId, "aix_00008_.png");

        Task stopping = host.OwnerStopAsync();
        await Task.Delay(700);
        Assert.True(host.State.Running);   // the result has not been collected yet

        Assert.Equal(200, (await Tunnel(host, "POST", "/aixman/purge", Http.Json(new { prompt_id = promptId }))).Status);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(host.State.Running);
    }

    [Fact]
    public async Task A_purge_of_an_older_job_does_not_end_a_stop_early()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var host = await SharingHost(dir, comfy);
        host.DrainGrace = TimeSpan.FromMinutes(10);
        host.Store.JobSubmitted("six-hours-old", "image", 4, false);
        host.Store.JobFinished("six-hours-old", true, "aix_00001_.png", null);
        comfy.Finished("six-hours-old", "aix_00001_.png");

        string promptId = Http.Body(await Tunnel(host, "POST", "/prompt", Http.ImagePrompt()))["prompt_id"]!.GetValue<string>();
        host.Runtime.Consume(Http.Started(promptId));
        host.Runtime.Consume(Http.Succeeded(promptId));
        comfy.Finished(promptId, "aix_00002_.png");

        Task stopping = host.OwnerStopAsync();
        // Seconds after the render: the node's own fallback, and aixman retrying an older purge.
        await host.Runtime.PurgeAsync("six-hours-old", CancellationToken.None);
        Assert.Equal(200, (await Tunnel(host, "POST", "/aixman/purge", Http.Json(new { prompt_id = "six-hours-old" }))).Status);
        await Task.Delay(800);
        Assert.True(host.State.Running);   // the new result has still not been collected

        Assert.Equal(200, (await Tunnel(host, "POST", "/aixman/purge", Http.Json(new { prompt_id = promptId }))).Status);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(host.State.Running);
    }

    [Fact]
    public async Task Undelivered_work_includes_a_result_aixman_has_not_collected()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));
        host.DrainGrace = TimeSpan.FromMinutes(10);
        await host.OwnerStartAsync();
        Assert.Null(host.UndeliveredWork);

        host.Runtime.TrackTunnelPrompt("p1");
        host.Runtime.Consume(Http.Started("p1"));
        Assert.Contains("เรนเดอร์", host.UndeliveredWork);

        // Finished, and not rendering any more — which is all the tray used to ask.
        host.Runtime.Consume(Http.Succeeded("p1"));
        Assert.False(host.Runtime.IsWorking);
        Assert.Contains("รอ pool เก็บผลงาน", host.UndeliveredWork);
    }

    [Fact]
    public async Task Stop_now_forfeits_and_closes_at_once()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));
        await host.OwnerStartAsync();
        host.Runtime.TrackTunnelPrompt("p1");

        await host.OwnerStopAsync(now: true);

        Assert.False(host.State.Running);
        Assert.False(host.State.Draining);
    }

    [Fact]
    public async Task Stop_now_ends_a_drain_already_waiting()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));
        await host.OwnerStartAsync();
        host.Runtime.TrackTunnelPrompt("p1");
        Task draining = host.OwnerStopAsync();

        await host.OwnerStopAsync(now: true);
        await draining.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(host.State.Running);
        Assert.False(host.State.Draining);
    }

    [Fact]
    public async Task The_headless_STOP_hands_the_render_over_before_it_ends()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));
        host.DrainGrace = TimeSpan.FromMilliseconds(300);
        using var now = new CancellationTokenSource();
        using var handOver = new CancellationTokenSource();

        Task running = host.RunAsync(now.Token, handOver.Token);
        await Eventually(() => host.State.Running);
        host.Runtime.TrackTunnelPrompt("p1");
        host.Runtime.Consume(Http.Started("p1"));

        handOver.Cancel();   // the agent's first Ctrl+C
        await Eventually(() => host.State.Draining);
        Assert.Equal(ReadyStage.Draining, host.Decide().Stage);

        // The relay stays open while the customer's render runs.
        await Task.Delay(600);
        Assert.False(running.IsCompleted);
        Assert.True(host.State.Running);

        host.Runtime.Consume(Http.Succeeded("p1"));
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(host.State.Running);
        Assert.False(host.State.Draining);
    }

    [Fact]
    public async Task A_second_headless_STOP_stops_without_waiting()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));
        host.DrainGrace = TimeSpan.FromMinutes(10);
        using var now = new CancellationTokenSource();
        using var handOver = new CancellationTokenSource();

        Task running = host.RunAsync(now.Token, handOver.Token);
        await Eventually(() => host.State.Running);
        host.Runtime.TrackTunnelPrompt("p1");
        host.Runtime.Consume(Http.Started("p1"));

        handOver.Cancel();
        await Eventually(() => host.State.Draining);
        now.Cancel();        // the second Ctrl+C

        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(host.State.Running);
        Assert.False(host.State.Draining);
    }

    [Fact]
    public async Task The_headless_STOP_with_nothing_in_flight_stops_at_once()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));
        using var handOver = new CancellationTokenSource();

        Task running = host.RunAsync(CancellationToken.None, handOver.Token);
        await Eventually(() => host.State.Running);

        handOver.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(host.State.Running);
    }

    // ---------------------------------------------- owner's settings gate

    [Fact]
    public async Task Unticked_job_kinds_are_not_advertised_when_auto_matching_is_off()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));
        host.UseAssessment(UsableReport("image", "video", "audio"));
        await host.OwnerStartAsync();

        Assert.True(host.Decide().Accept);
        Assert.Equal(new[] { "image", "video", "audio" }, host.HeartbeatPayload().CanRun);

        host.Settings.AutoMatch = false;
        host.Settings.AcceptedJobTypes = ["image", "upscale"];

        var heartbeat = host.HeartbeatPayload();
        Assert.Equal(new[] { "image" }, heartbeat.CanRun);
        Assert.Equal(new[] { "image" }, heartbeat.Lanes!.Keys);
        Assert.True(host.Decide().Accept);

        host.Settings.AcceptedJobTypes = ["text"];
        AcceptDecision nothing = host.Decide();
        Assert.False(nothing.Accept);
        Assert.Equal(ReadyStage.Paused, nothing.Stage);
        Assert.Empty(host.HeartbeatPayload().CanRun!);
    }

    [Fact]
    public async Task A_swapped_card_voids_the_report()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var host = new NodeHost(Paired(dir, comfy.Url));
        host.UseAssessment(UsableReport("image"));
        await host.OwnerStartAsync();

        await host.Runtime.ReadGpuHashAsync(CancellationToken.None);
        Assert.True(host.Decide().Accept);

        comfy.GpuName = "cuda:0 NVIDIA GeForce RTX 4090 : native";
        comfy.VramBytes = 24L * 1024 * 1024 * 1024;
        await host.Runtime.ReadGpuHashAsync(CancellationToken.None);

        AcceptDecision decision = host.Decide();
        Assert.False(decision.Accept);
        Assert.Equal(ReadyStage.Unassessed, decision.Stage);
        Assert.False(host.HeartbeatPayload().Assessed);
    }

    [Fact]
    public async Task The_heartbeat_says_when_the_node_is_busy()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var host = new NodeHost(Paired(dir, comfy.Url));
        host.UseAssessment(UsableReport("image"));
        await host.OwnerStartAsync();

        await host.Runtime.ReadQueueAsync(CancellationToken.None);
        var idle = host.HeartbeatPayload();
        Assert.False(idle.Busy);
        Assert.Equal(0, idle.QueueRemaining);
        Assert.True(idle.Accepting);

        comfy.Queue["owners-own"] = true;
        await host.Runtime.ReadQueueAsync(CancellationToken.None);
        var owners = host.HeartbeatPayload();
        Assert.True(owners.Busy);
        Assert.Equal(1, owners.QueueRemaining);

        // And on the wire, as fields the relay passes through.
        string json = JsonSerializer.Serialize(owners);
        Assert.Contains("\"busy\":true", json);
        Assert.Contains("\"queueRemaining\":1", json);

        comfy.Queue.Clear();
        await host.Runtime.ReadQueueAsync(CancellationToken.None);
        host.Runtime.TrackTunnelPrompt("customer");
        Assert.True(host.HeartbeatPayload().Busy);
    }

    // ------------------------------------------------------- reconciliation

    [Fact]
    public async Task A_render_ComfyUI_lost_is_written_off_and_stops_blocking_the_node()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var host = new NodeHost(Paired(dir, comfy.Url));
        host.Runtime.TrackTunnelPrompt("lost-1");
        host.Jobs.Submitted("lost-1", 4, "image");
        host.Runtime.Consume(Http.Started("lost-1"));
        Assert.True(host.Runtime.IsBusy);

        // One pass that finds it nowhere could be a race; two cannot.
        await host.ReconcileOnceAsync(CancellationToken.None);
        Assert.True(host.Runtime.IsBusy);
        await host.ReconcileOnceAsync(CancellationToken.None);

        Assert.False(host.Runtime.IsWorking);
        Assert.Null(host.Jobs.Current);
        Assert.Equal(JobStatus.Failed, host.Store.RecentJobs(5).Single(j => j.PromptId == "lost-1").Status);
    }

    [Fact]
    public async Task A_render_still_queued_is_left_alone()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var host = new NodeHost(Paired(dir, comfy.Url));
        host.Runtime.TrackTunnelPrompt("waiting");
        comfy.Queue["waiting"] = true;

        for (int i = 0; i < 3; i++) await host.ReconcileOnceAsync(CancellationToken.None);

        Assert.True(host.Runtime.HasTunnelWork);
    }

    [Fact]
    public async Task Nothing_is_written_off_while_ComfyUI_cannot_be_asked()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));   // ComfyUI on a closed port
        host.Runtime.TrackTunnelPrompt("unknown");

        for (int i = 0; i < 3; i++) await host.ReconcileOnceAsync(CancellationToken.None);

        Assert.True(host.Runtime.HasTunnelWork);
    }

    [Fact]
    public async Task Every_open_row_is_reconciled_not_only_the_oldest_twenty()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var host = new NodeHost(Paired(dir, comfy.Url));

        // Sixty old rows ComfyUI has forgotten, then the one it finished.
        for (int i = 0; i < 60; i++) host.Store.JobSubmitted($"old-{i:00}", "image", 4, false);
        host.Store.JobSubmitted("z-finished", "image", 4, false);
        comfy.Finished("z-finished", "aix_00042_.png");
        Backdate(dir, TimeSpan.FromHours(7));

        await host.ReconcileOnceAsync(CancellationToken.None);

        var jobs = host.Store.RecentJobs(100);
        Assert.Equal(JobStatus.Completed, jobs.Single(j => j.PromptId == "z-finished").Status);
        Assert.All(jobs.Where(j => j.PromptId.StartsWith("old-", StringComparison.Ordinal)), j => Assert.Equal(JobStatus.Failed, j.Status));
    }

    [Fact]
    public async Task A_purge_settles_a_job_the_socket_missed_before_its_history_goes()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var host = await SharingHost(dir, comfy);
        string output = dir.File("comfy/output/aix_00007_.png");

        string promptId = Http.Body(await Tunnel(host, "POST", "/prompt", Http.ImagePrompt()))["prompt_id"]!.GetValue<string>();
        // The progress socket was down: no events. ComfyUI finished it anyway,
        // and aixman collected it before the reconciler came round.
        comfy.Finished(promptId, "aix_00007_.png");
        LocalReply purged = await Tunnel(host, "POST", "/aixman/purge", Http.Json(new { prompt_id = promptId }));

        Assert.Equal(200, purged.Status);
        Assert.Equal(1, Http.Body(purged)["purged"]!.GetValue<int>());
        Assert.False(File.Exists(output));
        Assert.False(host.Runtime.IsWorking);
        Assert.Null(host.Jobs.Current);
        Assert.Equal(JobStatus.Completed, Job(host, promptId).Status);

        // The history it would have been settled from is gone. It must not be
        // written off as lost for that.
        Backdate(dir, TimeSpan.FromHours(7));
        await host.ReconcileOnceAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Completed, Job(host, promptId).Status);
    }

    [Fact]
    public async Task A_cached_render_that_ends_before_its_reply_is_read_still_lands_in_the_ledger()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        comfy.PromptDelay = TimeSpan.FromMilliseconds(400);
        await using var host = await SharingHost(dir, comfy);

        Task<LocalReply> submitting = Tunnel(host, "POST", "/prompt", Http.ImagePrompt());
        await Task.Delay(100);
        host.Runtime.Consume(Http.Started("prompt-1"));
        host.Runtime.Consume(Http.Succeeded("prompt-1"));
        Assert.Equal(200, (await submitting).Status);

        JobRecord job = Job(host, "prompt-1");
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.NotNull(job.StartedAt);
        Assert.Null(host.Jobs.Current);
        Assert.False(host.Runtime.IsWorking);
        Assert.Equal(200, (await Tunnel(host, "POST", "/prompt", Http.ImagePrompt())).Status);
    }

    private static void Backdate(TempDir dir, TimeSpan by)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(dir.Path, "node.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET submitted_at = submitted_at - $by";
        command.Parameters.AddWithValue("$by", (long)by.TotalMilliseconds);
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------- refused relay

    [Theory]
    [InlineData(401, "ลงทะเบียนเครื่องใหม่ในหน้า Settings")]
    [InlineData(403, "ปิดการใช้งานเครื่องนี้")]
    public async Task A_relay_that_refuses_the_worker_says_what_the_owner_has_to_do(int status, string hint)
    {
        using var dir = new TempDir();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var relay = builder.Build();
        relay.Map("/agent", (HttpContext context) => context.Response.StatusCode = status);
        await relay.StartAsync();

        string relayUrl = relay.Urls.First().Replace("http://", "ws://", StringComparison.Ordinal) + "/agent";
        await using var host = new NodeHost(Paired(dir) with { RelayUrl = relayUrl });

        await host.OwnerStartAsync();
        await Eventually(() => host.State.Connection == ConnectionState.Rejected);

        Assert.Contains(hint, host.State.ConnectionNote);
        // Still the owner's choice to share; retrying on its own will not fix
        // it, and it does not pretend to be reconnecting.
        Assert.True(host.State.Running);
        await Task.Delay(300);
        Assert.Equal(ConnectionState.Rejected, host.State.Connection);

        await host.OwnerStopAsync(now: true);
        Assert.Equal(ConnectionState.Stopped, host.State.Connection);
        Assert.Null(host.State.ConnectionNote);
    }

    // ---------------------------------------------------------------- pairing

    [Fact]
    public async Task Pairing_applies_the_identity_in_process_and_starts_sharing()
    {
        using var dir = new TempDir();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var studio = builder.Build();
        studio.MapPost("/api/v1/product/gpuxmine/claim", () => Results.Json(new
        {
            success = true,
            message = "ok",
            data = new { worker_id = "gxm-new", token = "new-token", relay_url = ClosedRelay },
        }));
        await studio.StartAsync();

        await using var host = new NodeHost(new NodeOptions
        {
            DataDirectory = dir.Path,
            RelayUrl = ClosedRelay,
            XmanStudioUrl = studio.Urls.First(),
            AutoUpdate = false,
        });
        Assert.False(host.ShouldResumeSharing);

        var (ok, _) = await host.PairAsync("ABCD1234");

        Assert.True(ok);
        Assert.Equal("gxm-new", host.Options.WorkerId);
        Assert.Equal("new-token", host.Options.Token);
        Assert.True(host.State.Running);
        Assert.True(host.Settings.SharingEnabled);
        Assert.True(File.Exists(NodeIdentityFile.PathIn(dir.Path)));
    }

    /// <summary>XMAN Studio's claim endpoint, answering with a new identity after <paramref name="delay"/>.</summary>
    private static async Task<WebApplication> StartStudioAsync(TimeSpan delay)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var studio = builder.Build();
        studio.MapPost("/api/v1/product/gpuxmine/claim", async () =>
        {
            await Task.Delay(delay);
            return Results.Json(new
            {
                success = true,
                message = "ok",
                data = new { worker_id = "gxm-new", token = "new-token", relay_url = ClosedRelay },
            });
        });
        await studio.StartAsync();
        return studio;
    }

    /// <summary>A relay that accepts the node's socket and never sends it anything: "connected", and nothing more.</summary>
    private static async Task<(WebApplication App, string AgentUrl)> StartQuietRelayAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var relay = builder.Build();
        relay.UseWebSockets();
        relay.Map("/agent", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];
            try
            {
                while (socket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var read = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (read.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                }
            }
            catch (Exception)
            {
                // The node went away.
            }
        });
        await relay.StartAsync();
        return (relay, relay.Urls.First().Replace("http://", "ws://", StringComparison.Ordinal) + "/agent");
    }

    [Fact]
    public async Task Pairing_takes_no_new_work_while_the_code_is_being_claimed()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var studio = await StartStudioAsync(TimeSpan.FromMilliseconds(800));
        await using var host = new NodeHost(Paired(dir, comfy.Url) with { XmanStudioUrl = studio.Urls.First() });
        host.UseAssessment(UsableReport("image"));
        await host.OwnerStartAsync();
        Assert.True(host.Decide().Accept);

        Task<(bool Ok, string Message)> pairing = host.PairAsync("ABCD1234");
        await Task.Delay(250);

        AcceptDecision during = host.Decide();
        Assert.False(during.Accept);
        Assert.Equal(ReadyStage.Draining, during.Stage);
        LocalReply prompt = await Tunnel(host, "POST", "/prompt", Http.ImagePrompt());
        Assert.Equal(503, prompt.Status);
        Assert.Equal(ReadyStage.Draining, Http.Body(prompt)["stage"]!.GetValue<string>());
        Assert.False(comfy.Reached("POST /prompt"));

        var (ok, _) = await pairing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(ok);
        Assert.Equal("gxm-new", host.Options.WorkerId);
        Assert.True(host.Decide().Accept);
    }

    [Fact]
    public async Task A_failed_claim_lets_the_node_take_work_again()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var host = new NodeHost(Paired(dir, comfy.Url));   // XMAN Studio on a closed port
        host.UseAssessment(UsableReport("image"));
        await host.OwnerStartAsync();

        var (ok, _) = await host.PairAsync("ABCD1234");

        Assert.False(ok);
        Assert.Equal("gxm-test", host.Options.WorkerId);
        Assert.True(host.Decide().Accept);
    }

    [Fact]
    public async Task Pairing_waits_for_the_old_identitys_last_result_to_be_collected()
    {
        using var dir = new TempDir();
        await using var comfy = await FakeComfy.StartAsync();
        await using var studio = await StartStudioAsync(TimeSpan.Zero);
        var (relay, agentUrl) = await StartQuietRelayAsync();
        await using var _ = relay;
        await using var host = new NodeHost(Paired(dir, comfy.Url) with { XmanStudioUrl = studio.Urls.First(), RelayUrl = agentUrl });
        host.DrainGrace = TimeSpan.FromMinutes(10);
        host.UseAssessment(UsableReport("image"));
        await host.OwnerStartAsync();
        await Eventually(() => host.State.Connection == ConnectionState.Connected);

        // A render that ended a moment ago, which aixman has not fetched yet.
        host.Runtime.TrackTunnelPrompt("p1");
        host.Runtime.Consume(Http.Started("p1"));
        host.Runtime.Consume(Http.Succeeded("p1"));
        comfy.Finished("p1", "aix_00001_.png");

        Task<(bool Ok, string Message)> pairing = host.PairAsync("ABCD1234");
        await Task.Delay(700);
        Assert.False(pairing.IsCompleted);
        Assert.Equal("gxm-test", host.Options.WorkerId);   // the old session is still up for aixman

        Assert.Equal(200, (await Tunnel(host, "POST", "/aixman/purge", Http.Json(new { prompt_id = "p1" }))).Status);
        var (ok, _) = await pairing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(ok);
        Assert.Equal("gxm-new", host.Options.WorkerId);
    }

    [Fact]
    public async Task Pairing_is_refused_before_the_code_is_spent_while_a_customer_job_runs()
    {
        using var dir = new TempDir();
        await using var host = new NodeHost(Paired(dir));
        host.Runtime.TrackTunnelPrompt("p1");

        var (ok, message) = await host.PairAsync("ABCD1234");

        Assert.False(ok);
        Assert.Contains("รหัสยังไม่ถูกใช้", message);
        Assert.Equal("gxm-test", host.Options.WorkerId);
    }
}
