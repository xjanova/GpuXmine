using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using GpuxMine.Core.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GpuxMine.Node.Tests;

/// <summary>
/// XMAN Studio, as far as the status call goes (contract C6): one endpoint
/// whose status code, body and timing the test sets, plus pairing so a test
/// can change the machine's identity while a status call is out.
/// </summary>
public sealed class FakeStudio : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string Url { get; }

    /// <summary>Bodies of POST .../status, as the node sent them.</summary>
    public ConcurrentQueue<string> StatusCalls { get; } = new();

    public int StatusCode { get; set; } = 200;
    public string Body { get; set; } = "{}";
    public string? CfRay { get; set; }

    /// <summary>
    /// Answers each status call from what it asked — for a test that plays
    /// the website's cursor. Overrides <see cref="StatusCode"/> and <see cref="Body"/>.
    /// </summary>
    public Func<JsonNode, (int Status, string Body)>? Reply { get; set; }

    /// <summary>Set to hold the status reply until the test lets it go.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Completed when a status call has arrived — for a test that holds it at <see cref="Gate"/>.</summary>
    public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FakeStudio(WebApplication app)
    {
        _app = app;
        Url = app.Urls.First();
    }

    public static async Task<FakeStudio> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        FakeStudio? fake = null;
        app.MapPost("/api/v1/product/gpuxmine/status", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            string asked = await reader.ReadToEndAsync();
            fake!.StatusCalls.Enqueue(asked);
            fake.Arrived.TrySetResult();
            if (fake.Gate is { } gate) await gate.Task;

            var (status, body) = fake.Reply is { } reply ? reply(JsonNode.Parse(asked)!) : (fake.StatusCode, fake.Body);
            context.Response.StatusCode = status;
            if (fake.CfRay is { } ray) context.Response.Headers["cf-ray"] = ray;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(body);
        });

        app.MapPost("/api/v1/product/gpuxmine/claim", () => Results.Json(new
        {
            success = true,
            message = "ok",
            data = new { worker_id = "gxm-new", token = "new-token", relay_url = "ws://127.0.0.1:9/agent" },
        }));

        await app.StartAsync();
        fake = new FakeStudio(app);
        return fake;
    }

    public async ValueTask DisposeAsync()
    {
        Gate?.TrySetResult();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// The node learning from XMAN Studio what the pool makes of it and what its
/// jobs paid: the client call, the ledger it writes, the loop's timing, and
/// the words the owner is shown.
/// </summary>
public class PoolStatusTests
{
    private const string ClosedRelay = "ws://127.0.0.1:9/agent";

    private static NodeOptions Paired(TempDir dir, string studioUrl) => new()
    {
        DataDirectory = dir.Path,
        WorkerId = "gxm-test",
        Token = "agent-token",
        RelayUrl = ClosedRelay,
        ComfyUrl = "http://127.0.0.1:9",
        XmanStudioUrl = studioUrl,
        AutoUpdate = false,
    };

    /// <summary>A whole C6 answer, shaped exactly as XMAN Studio's controller writes it.</summary>
    /// <param name="jobsMore">Set for an answer to <c>updated_after</c>; null is a website from before the cursor.</param>
    private static string Answer(
        string? dispatchStatus = "eligible",
        string? workerStatus = "ready",
        bool suspended = false,
        string? suspendedReason = null,
        bool? jobsMore = null,
        int holdHours = 24,
        params object[] jobs)
    {
        var data = new JsonObject
        {
            ["node"] = new JsonObject
            {
                ["dispatch_status"] = dispatchStatus,
                ["dispatch_note"] = "เครื่องผ่านเกณฑ์ ส่งงานได้",
                ["dispatch_worker_status"] = workerStatus,
                ["relay_online"] = true,
                ["suspended"] = suspended,
                ["suspended_reason"] = suspendedReason,
                ["last_seen_at"] = "2026-09-25T03:00:00+00:00",
            },
            ["earnings"] = new JsonObject
            {
                ["pending_satang"] = 1234,
                ["review_satang"] = 0,
                ["cleared_satang"] = 50,
                ["paid_satang"] = 10000,
                ["today_satang"] = 1284,
                ["month_satang"] = 11284,
                ["donated_satang_30d"] = 80,
                ["wallet_balance_satang"] = 25050,
                ["hold_hours"] = holdHours,
            },
            ["jobs"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(jobs)),
        };
        if (jobsMore is { } more) data["jobs_more"] = more;
        return new JsonObject { ["success"] = true, ["data"] = data }.ToJsonString();
    }

    private static object Job(string? promptId, long amount, string status, long donated = 0, string jobId = "aix-gpu-job-1", string? updatedAt = null)
    {
        var job = new Dictionary<string, object?>
        {
            ["job_id"] = jobId,
            ["prompt_id"] = promptId,
            ["kind"] = "image",
            ["amount_satang"] = amount,
            ["donated_value_satang"] = donated,
            ["status"] = status,
            ["completed_at"] = "2026-09-25T02:59:00+00:00",
        };
        // Only a website with the cursor sends it.
        if (updatedAt is not null) job["updated_at"] = updatedAt;
        return job;
    }

    private static void Finished(Storage.NodeStore store, string promptId, bool success = true)
    {
        store.JobSubmitted(promptId, "image", 4, false);
        store.JobFinished(promptId, success, success ? "aix_00001_.png" : null, success ? null : "boom");
    }

    // ------------------------------------------------------- the client call

    [Fact]
    public async Task StatusAsync_reads_the_whole_answer_in_satang()
    {
        await using var studio = await FakeStudio.StartAsync();
        studio.Body = Answer(jobs: [Job("p-1", 150, "paid", jobId: "aix-gpu-job-7")]);
        var client = new XmanStudioClient(new HttpClient(), studio.Url);

        NodeStatusResult result = await client.StatusAsync("gxm-test", "agent-token");

        Assert.Equal(StudioOutcome.Ok, result.Outcome);
        var status = result.Status!;
        Assert.Equal("eligible", status.Node!.DispatchStatus);
        Assert.Equal("ready", status.Node.WorkerStatus);
        Assert.True(status.Node.RelayOnline);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero), status.Node.LastSeenAt);
        Assert.Equal(25050L, status.Earnings!.WalletBalanceSatang);
        Assert.Equal(1234L, status.Earnings.PendingSatang);
        Assert.Equal(24, status.Earnings.HoldHours);
        SettledJob job = Assert.Single(status.Jobs!);
        Assert.Equal("p-1", job.PromptId);
        Assert.Equal(150L, job.AmountSatang);
        Assert.Equal("aix-gpu-job-7", job.JobId);

        // Authenticated with the worker id and the machine's own token.
        var sent = JsonNode.Parse(Assert.Single(studio.StatusCalls))!;
        Assert.Equal("gxm-test", sent["worker_id"]!.GetValue<string>());
        Assert.Equal("agent-token", sent["token"]!.GetValue<string>());
        // No cursor asked for, none sent: the call every website since C6 answers.
        Assert.Null(sent["updated_after"]);
        Assert.Null(status.JobsMore);
        Assert.Null(job.UpdatedAt);
    }

    [Fact]
    public async Task StatusAsync_sends_the_cursor_as_utc_to_the_second_and_reads_a_page()
    {
        await using var studio = await FakeStudio.StartAsync();
        // XMAN Studio writes its times in its own zone; Bangkok here.
        studio.Body = Answer(jobsMore: true, jobs: [Job("p-1", 150, "cleared", updatedAt: "2026-09-25T10:00:05+07:00")]);
        var client = new XmanStudioClient(new HttpClient(), studio.Url);

        NodeStatusResult result = await client.StatusAsync("gxm-test", "agent-token",
            new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.FromHours(7)));

        var sent = JsonNode.Parse(Assert.Single(studio.StatusCalls))!;
        Assert.Equal("2026-09-25T03:00:00+00:00", sent["updated_after"]!.GetValue<string>());
        Assert.Equal(StudioOutcome.Ok, result.Outcome);
        Assert.True(result.Status!.JobsMore);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 3, 0, 5, TimeSpan.Zero), Assert.Single(result.Status.Jobs!).UpdatedAt);
    }

    [Fact]
    public async Task A_refused_cursor_is_asked_again_the_old_way_and_not_read_as_a_refused_machine()
    {
        await using var studio = await FakeStudio.StartAsync();
        studio.Reply = asked => asked["updated_after"] is null
            ? (200, Answer(jobs: [Job("p-1", 150, "paid")]))
            : (422, """{"message":"updated_after ต้องเป็นวันเวลาแบบ ISO 8601","errors":{"updated_after":["updated_after ต้องเป็นวันเวลาแบบ ISO 8601"]}}""");
        var client = new XmanStudioClient(new HttpClient(), studio.Url);

        NodeStatusResult result = await client.StatusAsync("gxm-test", "agent-token", DateTimeOffset.UtcNow);

        Assert.Equal(StudioOutcome.Ok, result.Outcome);
        Assert.Equal("p-1", Assert.Single(result.Status!.Jobs!).PromptId);
        Assert.Equal(2, studio.StatusCalls.Count);
        Assert.Null(JsonNode.Parse(studio.StatusCalls.Last())!["updated_after"]);

        // A 422 about the machine itself is still a refused identity.
        studio.Reply = _ => (422, """{"message":"token is required","errors":{"token":["token is required"]}}""");
        Assert.Equal(StudioOutcome.IdentityRejected, (await client.StatusAsync("gxm-test", "agent-token", DateTimeOffset.UtcNow)).Outcome);
    }

    [Fact]
    public async Task A_field_the_website_did_not_send_is_unknown_not_zero()
    {
        await using var studio = await FakeStudio.StartAsync();
        studio.Body = """{"success":true,"data":{"node":{"dispatch_status":null,"suspended":false},"earnings":{"pending_satang":5},"jobs":[]}}""";
        var client = new XmanStudioClient(new HttpClient(), studio.Url);

        NodeStatusResult result = await client.StatusAsync("gxm-test", "agent-token");

        Assert.Equal(StudioOutcome.Ok, result.Outcome);
        Assert.Null(result.Status!.Earnings!.WalletBalanceSatang);
        Assert.Null(result.Status.Node!.LastSeenAt);
        Assert.Equal("—", PoolView.Baht(result.Status.Earnings.WalletBalanceSatang));
    }

    [Theory]
    [InlineData(401, StudioOutcome.IdentityRejected)]
    [InlineData(422, StudioOutcome.IdentityRejected)]
    [InlineData(404, StudioOutcome.NotSupported)]
    [InlineData(405, StudioOutcome.NotSupported)]
    [InlineData(429, StudioOutcome.Unavailable)]
    [InlineData(500, StudioOutcome.Unavailable)]
    [InlineData(502, StudioOutcome.Unavailable)]
    public async Task StatusAsync_tells_a_refused_identity_from_an_old_website_and_an_outage(int code, StudioOutcome expected)
    {
        await using var studio = await FakeStudio.StartAsync();
        studio.StatusCode = code;
        studio.Body = """{"success":false,"message":"ตัวตนเครื่องไม่ถูกต้อง"}""";
        studio.CfRay = "8c1d-SIN";
        var lines = new ConcurrentQueue<string>();
        var client = new XmanStudioClient(new HttpClient(), studio.Url, log: lines.Enqueue);

        NodeStatusResult result = await client.StatusAsync("gxm-test", "agent-token");

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Status);
        Assert.Equal(code, result.HttpStatus);
        // Soft is not quiet: the refusal is logged with what support needs.
        string line = Assert.Single(lines);
        Assert.Contains($"HTTP {code}", line);
        Assert.Contains("cf-ray 8c1d-SIN", line);
    }

    [Fact]
    public async Task An_unreadable_answer_or_no_website_at_all_is_an_outage()
    {
        await using var studio = await FakeStudio.StartAsync();
        studio.Body = "<html>Cloudflare</html>";
        var client = new XmanStudioClient(new HttpClient(), studio.Url);
        Assert.Equal(StudioOutcome.Unavailable, (await client.StatusAsync("gxm-test", "agent-token")).Outcome);

        var nowhere = new XmanStudioClient(new HttpClient(), "http://127.0.0.1:9");
        NodeStatusResult result = await nowhere.StatusAsync("gxm-test", "agent-token");
        Assert.Equal(StudioOutcome.Unavailable, result.Outcome);
        Assert.Contains("ติดต่อ XMAN Studio ไม่ได้", result.Message);
    }

    // ------------------------------------------------------------ the ledger

    [Fact]
    public void JobSettled_writes_only_what_changed()
    {
        using var dir = new TempDir();
        using var store = new Storage.NodeStore(Path.Combine(dir.Path, "node.db"));
        Finished(store, "p-1");

        Assert.True(store.JobSettled("p-1", 150, "pending", 0, "aix-gpu-job-7"));
        Assert.False(store.JobSettled("p-1", 150, "pending", 0, "aix-gpu-job-7"));   // the same answer, three minutes later
        Assert.True(store.JobSettled("p-1", 150, "paid", 0, "aix-gpu-job-7"));       // reached the wallet
        Assert.False(store.JobSettled("not-ours", 999, "paid"));                     // not a job this machine has on record
        Assert.False(store.JobSettled("", 999, "paid"));

        JobRecord job = store.RecentJobs(5).Single();
        Assert.Equal(1.50m, job.PayoutThb);
        Assert.Equal("paid", job.PayoutStatus);
        Assert.Equal(0m, job.DonatedThb);
    }

    [Fact]
    public void A_status_the_node_does_not_know_is_stored_as_unknown()
    {
        using var dir = new TempDir();
        using var store = new Storage.NodeStore(Path.Combine(dir.Path, "node.db"));
        Finished(store, "p-1");

        Assert.True(store.JobSettled("p-1", 150, "refunded"));

        JobRecord job = store.RecentJobs(5).Single();
        Assert.Equal(1.50m, job.PayoutThb);
        Assert.Null(job.PayoutStatus);
    }

    [Fact]
    public void Voided_and_unsettled_jobs_are_not_counted_as_earned()
    {
        using var dir = new TempDir();
        using var store = new Storage.NodeStore(Path.Combine(dir.Path, "node.db"));
        var midnight = DateTimeOffset.UtcNow.AddHours(-1);

        Finished(store, "voided");
        store.JobSettled("voided", 200, "void");
        // Only a voided job settled: nothing has been earned, which is a dash.
        Assert.Null(store.TotalsFor(midnight).EarnedSatang);
        Assert.Null(store.Totals(midnight).EarnedThb);

        Finished(store, "paid");
        store.JobSettled("paid", 150, "paid");
        Finished(store, "free");
        store.JobSettled("free", 0, "pending", donatedSatang: 80);
        Finished(store, "waiting");
        Finished(store, "broke", success: false);

        LedgerTotals today = store.TotalsFor(midnight);
        Assert.Equal(4, today.Completed);
        Assert.Equal(1, today.Failed);
        Assert.Equal(150L, today.EarnedSatang);
        Assert.Equal(2, today.Settled);
        Assert.Equal(1, today.Unsettled);
        Assert.Equal(80L, today.DonatedSatang);
        Assert.Equal(1.50m, store.Totals(midnight).EarnedThb);

        JobTotals history = store.TotalsSince(30);
        Assert.Equal(150L, history.PayoutSatang);
        Assert.Equal(2, history.Settled);
        Assert.Equal(1, history.Unsettled);
        Assert.Equal(80L, history.DonatedSatang);
    }

    [Fact]
    public void A_ledger_from_before_settlements_gains_the_columns_and_keeps_its_rows()
    {
        using var dir = new TempDir();
        string db = Path.Combine(dir.Path, "node.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // The jobs table as the first shipped build created it.
            command.CommandText = """
                CREATE TABLE jobs (prompt_id TEXT PRIMARY KEY, kind TEXT NOT NULL, status TEXT NOT NULL,
                    nodes_total INTEGER NOT NULL DEFAULT 0, submitted_at INTEGER NOT NULL, started_at INTEGER,
                    completed_at INTEGER, output_file TEXT, error TEXT, payout_satang INTEGER,
                    free_share INTEGER NOT NULL DEFAULT 0);
                INSERT INTO jobs (prompt_id, kind, status, submitted_at, completed_at) VALUES ('old', 'image', 'completed', 1, 2);
                """;
            command.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var store = new Storage.NodeStore(db);

        Assert.True(store.JobSettled("old", 42, "cleared"));
        JobRecord job = store.RecentJobs(5).Single();
        Assert.Equal(0.42m, job.PayoutThb);
        Assert.Equal("cleared", job.PayoutStatus);
    }

    // ------------------------------------------------------------ the loop

    [Fact]
    public async Task A_status_poll_writes_this_machines_payouts_into_the_ledger_once()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        var log = new NullLog();
        await using var host = new NodeHost(Paired(dir, studio.Url), alsoLogTo: log);
        Finished(host.Store, "p-paid");
        Finished(host.Store, "p-free");
        studio.Body = Answer(jobs:
        [
            Job("p-paid", 150, "paid", jobId: "aix-gpu-job-7"),
            Job("p-free", 0, "pending", donated: 80, jobId: "aix-gpu-job-8"),
            Job("not-this-machine", 999, "paid", jobId: "aix-gpu-job-9"),
            Job(null, 500, "paid", jobId: "aix-gpu-job-10"),   // aixman wrote no prompt id — cannot be matched
        ]);

        TimeSpan next = await host.PollPoolStatusOnceAsync(CancellationToken.None);

        Assert.Equal(NodeHost.PoolPollStopped, next);   // not sharing: every fifteen minutes
        Assert.Equal(PoolOutcome.Ok, host.State.Pool.Outcome);
        Assert.Equal(2, host.State.Pool.LedgerChanges);
        Assert.Equal(25050L, host.State.Pool.Earnings!.WalletBalanceSatang);

        var jobs = host.Store.RecentJobs(10).ToDictionary(j => j.PromptId);
        Assert.Equal(1.50m, jobs["p-paid"].PayoutThb);
        Assert.Equal("paid", jobs["p-paid"].PayoutStatus);
        Assert.Equal(0m, jobs["p-free"].PayoutThb);
        Assert.Equal(0.80m, jobs["p-free"].DonatedThb);
        Assert.Single(log.Lines, l => l.StartsWith("[pay]", StringComparison.Ordinal));

        // The same answer again: nothing moved, nothing said.
        await host.PollPoolStatusOnceAsync(CancellationToken.None);
        Assert.Equal(0, host.State.Pool.LedgerChanges);
        Assert.Single(log.Lines, l => l.StartsWith("[pay]", StringComparison.Ordinal));
        Assert.Single(log.Lines, l => l.StartsWith("[pool]", StringComparison.Ordinal));

        // Sharing: every three minutes.
        await host.OwnerStartAsync();
        Assert.Equal(NodeHost.PoolPollSharing, await host.PollPoolStatusOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_identity_says_re_pair_and_waits_half_an_hour()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        var log = new NullLog();
        await using var host = new NodeHost(Paired(dir, studio.Url), alsoLogTo: log);
        studio.Body = Answer();
        await host.PollPoolStatusOnceAsync(CancellationToken.None);

        studio.StatusCode = 401;
        studio.Body = """{"success":false,"message":"ตัวตนเครื่องไม่ถูกต้อง"}""";
        TimeSpan next = await host.PollPoolStatusOnceAsync(CancellationToken.None);

        Assert.Equal(NodeHost.PoolPollRejected, next);
        PoolView pool = host.State.Pool;
        Assert.Equal(PoolOutcome.IdentityRejected, pool.Outcome);
        Assert.Null(pool.Last);   // about an identity the website has let go of
        Assert.True(pool.Blocked);
        Assert.Contains("ลงทะเบียนเครื่องใหม่ในหน้า Settings", pool.Alert);
        Assert.Contains(log.Lines, l => l.StartsWith("WARN [pool]", StringComparison.Ordinal) && l.Contains("ลงทะเบียนเครื่องใหม่"));
    }

    [Fact]
    public async Task An_outage_keeps_the_last_answer_and_backs_off_to_half_an_hour()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        var log = new NullLog();
        await using var host = new NodeHost(Paired(dir, studio.Url), alsoLogTo: log);
        await host.OwnerStartAsync();
        studio.Body = Answer();
        await host.PollPoolStatusOnceAsync(CancellationToken.None);

        studio.StatusCode = 502;
        studio.Body = "bad gateway";
        var waits = new List<double>();
        for (int i = 0; i < 6; i++) waits.Add((await host.PollPoolStatusOnceAsync(CancellationToken.None)).TotalMinutes);

        Assert.Equal(new double[] { 3, 6, 12, 24, 30, 30 }, waits);
        PoolView pool = host.State.Pool;
        Assert.Equal(PoolOutcome.Unavailable, pool.Outcome);
        Assert.NotNull(pool.Last);   // the website being down says nothing new about the machine
        Assert.Equal("อยู่ใน pool · พร้อมรับงาน", pool.Headline);
        Assert.NotNull(pool.LastOkAt);
        // One line for the outage, not one per attempt.
        Assert.Single(log.Lines, l => l.Contains("อ่านสถานะจาก XMAN Studio ไม่ได้", StringComparison.Ordinal));

        // Back again: the backoff starts over.
        studio.StatusCode = 200;
        studio.Body = Answer();
        Assert.Equal(NodeHost.PoolPollSharing, await host.PollPoolStatusOnceAsync(CancellationToken.None));
        studio.StatusCode = 500;
        Assert.Equal(3, (await host.PollPoolStatusOnceAsync(CancellationToken.None)).TotalMinutes);
    }

    [Fact]
    public async Task A_website_from_before_the_status_call_is_asked_hourly()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        await using var host = new NodeHost(Paired(dir, studio.Url));
        studio.StatusCode = 404;
        studio.Body = """{"message":"Not Found"}""";

        Assert.Equal(NodeHost.PoolPollNotSupported, await host.PollPoolStatusOnceAsync(CancellationToken.None));
        Assert.Equal(PoolOutcome.NotSupported, host.State.Pool.Outcome);
        Assert.False(host.State.Pool.Blocked);
    }

    [Fact]
    public async Task An_unpaired_node_does_not_ask()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        await using var host = new NodeHost(new NodeOptions
        {
            DataDirectory = dir.Path,
            RelayUrl = ClosedRelay,
            XmanStudioUrl = studio.Url,
            AutoUpdate = false,
        });

        await host.PollPoolStatusOnceAsync(CancellationToken.None);

        Assert.Equal(PoolOutcome.Unpaired, host.State.Pool.Outcome);
        Assert.Empty(studio.StatusCalls);
    }

    [Fact]
    public async Task An_answer_about_an_identity_replaced_mid_call_is_thrown_away()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        await using var host = new NodeHost(Paired(dir, studio.Url));
        Finished(host.Store, "p-1");
        studio.Body = Answer(jobs: [Job("p-1", 150, "paid")]);
        studio.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<TimeSpan> polling = host.PollPoolStatusOnceAsync(CancellationToken.None);
        await studio.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var (paired, _) = await host.PairAsync("ABCD1234");
        Assert.True(paired);
        studio.Gate.SetResult();
        TimeSpan next = await polling;

        Assert.Equal(TimeSpan.Zero, next);   // ask again, about the machine it is now
        Assert.Equal(PoolOutcome.NotAsked, host.State.Pool.Outcome);
        Assert.Null(host.Store.RecentJobs(5).Single().PayoutThb);
    }

    // ----------------------------------------------------------- the cursor

    /// <summary>
    /// gpu_job_earnings for one machine, answered the way
    /// GpuxMineNodeController::status answers: without a cursor the latest
    /// fifty; with one, what changed after it, oldest first, two hundred at a
    /// time, with <c>jobs_more</c>. Rows are in the order the jobs finished.
    /// </summary>
    private sealed class EarningsTable
    {
        public sealed record Row(string PromptId, DateTimeOffset UpdatedAt, string Status, long Amount);

        public List<Row> Rows { get; } = [];
        public int HoldHours { get; set; } = 24;

        /// <summary>Which call (1-based) is throttled instead of answered.</summary>
        public int? ThrottleCall { get; set; }

        private int _calls;

        public (int, string) Answer(JsonNode asked)
        {
            if (++_calls == ThrottleCall)
                return (429, """{"success":false,"message":"ถามสถานะเครื่องถี่เกินไป — รอสักครู่แล้วลองใหม่"}""");

            if (asked["updated_after"]?.GetValue<string>() is not { } after)
            {
                var latest = Enumerable.Reverse(Rows).Take(50).Select(Job).ToArray();
                return (200, PoolStatusTests.Answer(holdHours: HoldHours, jobs: latest));
            }

            DateTimeOffset cursor = DateTimeOffset.Parse(after, CultureInfo.InvariantCulture);
            var page = Rows.Where(r => r.UpdatedAt > cursor).OrderBy(r => r.UpdatedAt).Take(200).ToList();
            return (200, PoolStatusTests.Answer(jobsMore: page.Count >= 200, holdHours: HoldHours, jobs: page.Select(Job).ToArray()));
        }

        // Written in Bangkok time, as the website may: the offset must not matter.
        private static object Job(Row row) => PoolStatusTests.Job(row.PromptId, row.Amount, row.Status,
            jobId: "aix-gpu-job-" + row.PromptId,
            updatedAt: row.UpdatedAt.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
    }

    /// <summary>A moment as the website stores it: whole seconds.</summary>
    private static DateTimeOffset WholeSecond(DateTimeOffset at) => DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds());

    private static DateTimeOffset? AskedAfter(string call) =>
        JsonNode.Parse(call)!["updated_after"]?.GetValue<string>() is { } text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture)
            : null;

    private static void Near(DateTimeOffset expected, DateTimeOffset? actual)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value, expected - TimeSpan.FromSeconds(30), expected + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Every_changed_job_is_read_page_by_page_from_where_the_last_pass_stopped()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        var log = new NullLog();
        await using var host = new NodeHost(Paired(dir, studio.Url), alsoLogTo: log);
        var table = new EarningsTable();
        DateTimeOffset start = WholeSecond(DateTimeOffset.UtcNow.AddHours(-60));
        for (int i = 0; i < 700; i++)
        {
            Finished(host.Store, $"p-{i}");
            table.Rows.Add(new($"p-{i}", start.AddSeconds(i), "pending", 100 + i));
        }
        studio.Reply = table.Answer;

        DateTimeOffset began = DateTimeOffset.UtcNow;
        TimeSpan next = await host.PollPoolStatusOnceAsync(CancellationToken.None);

        // A worker with no cursor starts one hold and two days back, then
        // asks after the newest job each page listed — two pages a pass.
        var asked = studio.StatusCalls.Select(AskedAfter).ToArray();
        Assert.Equal(NodeHost.PoolPagesPerPass, asked.Length);
        Near(began - TimeSpan.FromHours(24 + 48), asked[0]);
        Assert.Equal(start.AddSeconds(199), asked[1]);
        Assert.Equal(400, host.State.Pool.LedgerChanges);
        Assert.Single(log.Lines, l => l.StartsWith("[pay]", StringComparison.Ordinal));   // one line a pass, not one a page
        // More to read: back in a minute, not in the stopped machine's fifteen.
        Assert.Equal(NodeHost.PoolCatchUp, next);

        // The next pass carries on from there, and reaches the end.
        Assert.Equal(NodeHost.PoolPollStopped, await host.PollPoolStatusOnceAsync(CancellationToken.None));
        var carried = studio.StatusCalls.Skip(2).Select(AskedAfter).ToArray();
        Assert.Equal(new DateTimeOffset?[] { start.AddSeconds(399), start.AddSeconds(599) }, carried);
        Assert.Equal(300, host.State.Pool.LedgerChanges);
        Assert.All(host.Store.RecentJobs(1000), j => Assert.Equal("pending", j.PayoutStatus));
        Assert.Contains("gxm-test", host.Store.GetSetting(NodeHost.PoolCursorSetting));

        // One of the oldest jobs reaches the wallet — far outside the latest
        // fifty, which is all the call used to show — and is seen.
        table.Rows[3] = table.Rows[3] with { Status = "paid", UpdatedAt = start.AddSeconds(5000) };
        await host.PollPoolStatusOnceAsync(CancellationToken.None);
        Assert.Equal(start.AddSeconds(699), AskedAfter(studio.StatusCalls.Last()));
        Assert.Equal(1, host.State.Pool.LedgerChanges);
        Assert.Equal("paid", host.Store.RecentJobs(1000).Single(j => j.PromptId == "p-3").PayoutStatus);
    }

    [Fact]
    public async Task A_page_that_fails_mid_backlog_keeps_what_was_read_and_carries_on_next_pass()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        await using var host = new NodeHost(Paired(dir, studio.Url));
        var table = new EarningsTable { ThrottleCall = 2 };
        DateTimeOffset start = WholeSecond(DateTimeOffset.UtcNow.AddHours(-10));
        for (int i = 0; i < 250; i++)
        {
            Finished(host.Store, $"p-{i}");
            table.Rows.Add(new($"p-{i}", start.AddSeconds(i), "cleared", 50));
        }
        studio.Reply = table.Answer;

        TimeSpan next = await host.PollPoolStatusOnceAsync(CancellationToken.None);

        Assert.Equal(PoolOutcome.Ok, host.State.Pool.Outcome);   // the first page was an answer
        Assert.Equal(200, host.State.Pool.LedgerChanges);
        Assert.Equal(NodeHost.PoolCatchUp, next);

        await host.PollPoolStatusOnceAsync(CancellationToken.None);
        Assert.Equal(start.AddSeconds(199), AskedAfter(studio.StatusCalls.Last()));
        Assert.Equal(50, host.State.Pool.LedgerChanges);
        Assert.All(host.Store.RecentJobs(1000), j => Assert.Equal("cleared", j.PayoutStatus));
    }

    [Fact]
    public async Task A_new_pairing_reads_its_own_jobs_from_the_start_not_after_the_old_workers_cursor()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        await using var host = new NodeHost(Paired(dir, studio.Url));
        var table = new EarningsTable();
        table.Rows.Add(new("p-1", WholeSecond(DateTimeOffset.UtcNow.AddMinutes(-30)), "pending", 150));
        studio.Reply = table.Answer;
        await host.PollPoolStatusOnceAsync(CancellationToken.None);
        Assert.Contains("gxm-test", host.Store.GetSetting(NodeHost.PoolCursorSetting));

        var (paired, _) = await host.PairAsync("ABCD1234");
        Assert.True(paired);
        DateTimeOffset began = DateTimeOffset.UtcNow;
        await host.PollPoolStatusOnceAsync(CancellationToken.None);

        JsonNode last = JsonNode.Parse(studio.StatusCalls.Last())!;
        Assert.Equal("gxm-new", last["worker_id"]!.GetValue<string>());
        Near(began - TimeSpan.FromHours(24 + 48), AskedAfter(studio.StatusCalls.Last()));
        Assert.Contains("gxm-new", host.Store.GetSetting(NodeHost.PoolCursorSetting));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"Worker":"gxm-test","After":-5}""")]
    [InlineData("""{"Worker":"gxm-test","After":99999999999}""")]
    public async Task A_cursor_that_cannot_be_right_is_a_fresh_start(string kept)
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        await using var host = new NodeHost(Paired(dir, studio.Url));
        studio.Reply = new EarningsTable().Answer;
        host.Store.SetSetting(NodeHost.PoolCursorSetting, kept);

        DateTimeOffset began = DateTimeOffset.UtcNow;
        await host.PollPoolStatusOnceAsync(CancellationToken.None);

        Near(began - TimeSpan.FromHours(24 + 48), AskedAfter(Assert.Single(studio.StatusCalls)));
        Assert.Equal(PoolOutcome.Ok, host.State.Pool.Outcome);
    }

    [Fact]
    public async Task A_website_holding_longer_than_a_day_widens_the_first_start()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        await using var host = new NodeHost(Paired(dir, studio.Url));
        var table = new EarningsTable { HoldHours = 72 };
        // Finished four days ago and still inside a 72-hour hold plus two days.
        DateTimeOffset old = WholeSecond(DateTimeOffset.UtcNow.AddHours(-100));
        Finished(host.Store, "p-old");
        table.Rows.Add(new("p-old", old, "cleared", 150));
        studio.Reply = table.Answer;

        DateTimeOffset began = DateTimeOffset.UtcNow;
        await host.PollPoolStatusOnceAsync(CancellationToken.None);

        var asked = studio.StatusCalls.Select(AskedAfter).ToArray();
        Assert.Equal(2, asked.Length);
        Near(began - TimeSpan.FromHours(24 + 48), asked[0]);
        Near(began - TimeSpan.FromHours(72 + 48), asked[1]);
        Assert.Equal("cleared", host.Store.RecentJobs(5).Single().PayoutStatus);

        // From then on it reads after the newest job it has seen.
        await host.PollPoolStatusOnceAsync(CancellationToken.None);
        Assert.Equal(old, AskedAfter(studio.StatusCalls.Last()));
    }

    [Fact]
    public async Task A_website_that_ignores_the_cursor_is_read_the_old_way_and_a_kept_cursor_survives_it()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        await using var host = new NodeHost(Paired(dir, studio.Url));
        Finished(host.Store, "p-1");
        Finished(host.Store, "p-2");
        // No jobs_more and no updated_at: a website from before the cursor.
        studio.Body = Answer(jobs: [Job("p-1", 150, "paid")]);

        DateTimeOffset began = DateTimeOffset.UtcNow;
        Assert.Equal(NodeHost.PoolPollStopped, await host.PollPoolStatusOnceAsync(CancellationToken.None));
        await host.PollPoolStatusOnceAsync(CancellationToken.None);

        // One call a pass, exactly as before, and nothing kept to send next time.
        Assert.Equal(2, studio.StatusCalls.Count);
        Assert.All(studio.StatusCalls, c => Near(began - TimeSpan.FromHours(24 + 48), AskedAfter(c)));
        Assert.Null(host.Store.GetSetting(NodeHost.PoolCursorSetting));
        Assert.Equal("paid", host.Store.RecentJobs(5).Single(j => j.PromptId == "p-1").PayoutStatus);

        // Upgraded: a cursor is kept …
        DateTimeOffset seen = WholeSecond(DateTimeOffset.UtcNow.AddMinutes(-5));
        studio.Body = Answer(jobsMore: false, jobs: [Job("p-2", 90, "pending", updatedAt: seen.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture))]);
        await host.PollPoolStatusOnceAsync(CancellationToken.None);
        Assert.Contains($"{seen.ToUnixTimeSeconds()}", host.Store.GetSetting(NodeHost.PoolCursorSetting));

        // … and rolled back: the old way again, and the cursor waits for the next upgrade.
        studio.Body = Answer(jobs: [Job("p-2", 90, "cleared")]);
        await host.PollPoolStatusOnceAsync(CancellationToken.None);
        Assert.Equal(seen, AskedAfter(studio.StatusCalls.Last()));
        Assert.Equal("cleared", host.Store.RecentJobs(5).Single(j => j.PromptId == "p-2").PayoutStatus);
        Assert.Contains($"{seen.ToUnixTimeSeconds()}", host.Store.GetSetting(NodeHost.PoolCursorSetting));
    }

    // ---------------------------------------------------------- the wording

    private static PoolView Answered(NodeDispatch node) => new()
    {
        Outcome = PoolOutcome.Ok,
        Last = new NodeStatus { Node = node },
        LastOkAt = DateTimeOffset.Now,
    };

    [Fact]
    public void A_node_the_pool_takes_work_from_says_so_and_asks_nothing_of_the_owner()
    {
        PoolView pool = Answered(new NodeDispatch { DispatchStatus = "eligible", WorkerStatus = "ready", DispatchNote = "ok" });

        Assert.Equal("อยู่ใน pool · พร้อมรับงาน", pool.Headline);
        Assert.Equal("ok", pool.Detail);
        Assert.Null(pool.Alert);
        Assert.False(pool.Blocked);
    }

    [Fact]
    public void A_suspended_node_is_blocked_and_says_why()
    {
        PoolView pool = Answered(new NodeDispatch
        {
            DispatchStatus = "suspended",
            WorkerStatus = "terminated",
            Suspended = true,
            SuspendedReason = "ผลงานไม่ผ่านการตรวจ",
        });

        Assert.True(pool.Suspended);
        Assert.True(pool.Blocked);
        Assert.Equal("ถูกระงับโดย XMAN Studio", pool.Headline);
        Assert.Contains("ผลงานไม่ผ่านการตรวจ", pool.Alert);
        Assert.Contains("ผลงานไม่ผ่านการตรวจ", pool.Detail);
    }

    [Theory]
    [InlineData("retired", "ปลด")]
    [InlineData("rejected", "ลงทะเบียนเครื่องใหม่")]
    public void A_node_the_pool_has_let_go_of_tells_the_owner_what_to_do(string dispatch, string hint)
    {
        PoolView pool = Answered(new NodeDispatch { DispatchStatus = dispatch });

        Assert.True(pool.Blocked);
        Assert.Contains(hint, pool.Alert);
    }

    [Theory]
    [InlineData("eligible", "busy", "กำลังทำงาน")]
    [InlineData("eligible", "warming", "ตรวจความพร้อม")]
    [InlineData("eligible", "terminated", "พักเครื่องนี้ไว้")]
    [InlineData("unassessed", null, "ผลประเมิน")]
    [InlineData("no-matching-model", null, "ยังไม่มีงานที่ตรงกับเครื่องนี้")]
    [InlineData("offline", null, "ออฟไลน์")]
    [InlineData("error", null, "ลองใหม่เอง")]
    [InlineData(null, null, "ยังไม่ได้ส่งเครื่องนี้เข้า pool")]
    [InlineData("something-new", null, "something-new")]
    public void Every_verdict_reads_as_words_and_none_of_these_block(string? dispatch, string? worker, string expected)
    {
        PoolView pool = Answered(new NodeDispatch { DispatchStatus = dispatch, WorkerStatus = worker });

        Assert.Contains(expected, pool.Headline);
        Assert.False(pool.Blocked);
        Assert.Null(pool.Alert);
    }

    // ------------------------------------------------ a lifted suspension

    /// <summary>A relay that answers the node's dial 403 worker-disabled while <see cref="Refuse"/> is set, and otherwise holds the socket open.</summary>
    private sealed class DisablingRelay : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _attempts;

        public volatile bool Refuse = true;
        public int Attempts => Volatile.Read(ref _attempts);
        public string AgentUrl { get; }

        private DisablingRelay(WebApplication app)
        {
            _app = app;
            AgentUrl = app.Urls.First().Replace("http://", "ws://", StringComparison.Ordinal) + "/agent";
        }

        public static async Task<DisablingRelay> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.UseWebSockets();
            DisablingRelay? relay = null;
            app.Map("/agent", async (HttpContext context) =>
            {
                Interlocked.Increment(ref relay!._attempts);
                if (relay.Refuse)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "worker-disabled" });
                    return;
                }
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
            await app.StartAsync();
            relay = new DisablingRelay(app);
            return relay;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("the node did not get there in time");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_lifted_suspension_reconnects_at_once_instead_of_waiting_out_the_relays_403()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        await using var relay = await DisablingRelay.StartAsync();
        studio.Body = Answer(dispatchStatus: "suspended", suspended: true, suspendedReason: "ตรวจสอบผลงาน");
        await using var host = new NodeHost(Paired(dir, studio.Url) with { RelayUrl = relay.AgentUrl });

        await host.OwnerStartAsync();
        await Eventually(() => relay.Attempts == 1 && host.State.Connection == ConnectionState.Rejected);
        await host.PollPoolStatusOnceAsync(CancellationToken.None);   // still suspended: nothing changes
        await Task.Delay(300);
        Assert.Equal(1, relay.Attempts);

        // The admin resumes it: the relay lets the worker in again, and XMAN
        // Studio says so on the next status call. The node's own wait after
        // the 403 has minutes left to run.
        relay.Refuse = false;
        studio.Body = Answer();
        await host.PollPoolStatusOnceAsync(CancellationToken.None);

        await Eventually(() => relay.Attempts == 2 && host.State.Connection == ConnectionState.Connected);
    }

    [Fact]
    public async Task A_short_suspension_the_status_call_never_saw_still_ends_at_the_next_call()
    {
        using var dir = new TempDir();
        await using var studio = await FakeStudio.StartAsync();
        await using var relay = await DisablingRelay.StartAsync();
        studio.Body = Answer();   // suspended and resumed between two status calls
        await using var host = new NodeHost(Paired(dir, studio.Url) with { RelayUrl = relay.AgentUrl });

        await host.OwnerStartAsync();
        await Eventually(() => relay.Attempts == 1 && host.State.Connection == ConnectionState.Rejected);

        // The relay has not caught up yet: one knock for the status call, not a loop.
        await host.PollPoolStatusOnceAsync(CancellationToken.None);
        await Eventually(() => relay.Attempts == 2);
        await Task.Delay(600);
        Assert.Equal(2, relay.Attempts);

        relay.Refuse = false;   // sync-nodes has enabled the worker again
        await host.PollPoolStatusOnceAsync(CancellationToken.None);
        await Eventually(() => relay.Attempts == 3 && host.State.Connection == ConnectionState.Connected);

        // Connected, a status call has nothing to cut short.
        await host.PollPoolStatusOnceAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(3, relay.Attempts);
    }

    [Fact]
    public void Money_is_a_dash_until_it_is_known_and_never_a_float()
    {
        Assert.Equal("—", PoolView.Baht(null));
        Assert.Equal($"฿{0m:N2}", PoolView.Baht(0));
        Assert.Equal($"฿{1234.56m:N2}", PoolView.Baht(123456));
        Assert.Equal($"฿{0.01m:N2}", PoolView.Baht(1));
    }

    [Fact]
    public void Settlement_states_read_as_where_the_money_is()
    {
        Assert.Equal("พัก 24 ชม.", PoolView.DescribePayoutStatus("pending", 24));
        Assert.Equal("รอตรวจสอบ", PoolView.DescribePayoutStatus("review"));
        Assert.Equal("รอเข้ากระเป๋า", PoolView.DescribePayoutStatus("cleared"));
        Assert.Equal("เข้ากระเป๋าแล้ว", PoolView.DescribePayoutStatus("paid"));
        Assert.Equal("ยกเลิก", PoolView.DescribePayoutStatus("void"));
        Assert.Equal("รอตัดยอด", PoolView.DescribePayoutStatus(null));
    }
}
