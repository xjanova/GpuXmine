using System.Collections.Concurrent;
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
            fake!.StatusCalls.Enqueue(await reader.ReadToEndAsync());
            fake.Arrived.TrySetResult();
            if (fake.Gate is { } gate) await gate.Task;

            context.Response.StatusCode = fake.StatusCode;
            if (fake.CfRay is { } ray) context.Response.Headers["cf-ray"] = ray;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(fake.Body);
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
    private static string Answer(
        string? dispatchStatus = "eligible",
        string? workerStatus = "ready",
        bool suspended = false,
        string? suspendedReason = null,
        params object[] jobs) => new JsonObject
    {
        ["success"] = true,
        ["data"] = new JsonObject
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
                ["hold_hours"] = 24,
            },
            ["jobs"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(jobs)),
        },
    }.ToJsonString();

    private static object Job(string? promptId, long amount, string status, long donated = 0, string jobId = "aix-gpu-job-1") => new Dictionary<string, object?>
    {
        ["job_id"] = jobId,
        ["prompt_id"] = promptId,
        ["kind"] = "image",
        ["amount_satang"] = amount,
        ["donated_value_satang"] = donated,
        ["status"] = status,
        ["completed_at"] = "2026-09-25T02:59:00+00:00",
    };

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
