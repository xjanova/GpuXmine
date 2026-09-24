using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using GpuxMine.Protocol;

namespace GpuxMine.Relay.Tests;

/// <summary>
/// The relay between aixman and a node, driven with raw frames: a v0.1 agent's
/// single reply, a current agent's streamed one, and the frames a broken or
/// hostile agent might send.
/// </summary>
public class TunnelFramingTests
{
    /// <summary>A relay with one enrolled worker whose agent is connected and registered.</summary>
    private sealed class Connected(RelayTestHost relay, Enrolled worker, FakeAgent agent) : IAsyncDisposable
    {
        public RelayTestHost Relay { get; } = relay;
        public Enrolled Worker { get; } = worker;
        public FakeAgent Agent { get; } = agent;

        public async ValueTask DisposeAsync()
        {
            await Agent.DisposeAsync();
            await Relay.DisposeAsync();
        }
    }

    private static async Task<Connected> ConnectedAsync(IDictionary<string, string?>? settings = null)
    {
        var relay = await RelayTestHost.StartAsync(settings);
        Enrolled worker = await relay.EnrollAsync();
        FakeAgent agent = await relay.ConnectAgentAsync(worker.WorkerId, worker.Token!);
        await relay.WaitOnlineAsync(worker.WorkerId);
        return new Connected(relay, worker, agent);
    }

    /// <summary>The reply was cut off: either no response at all, or a body that fails part-way.</summary>
    private static Task AssertCutOffAsync(Task<HttpResponseMessage> call)
        => Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using HttpResponseMessage response = await call;
            await response.Content.ReadAsByteArrayAsync();
        });

    private static Task<HttpResponseMessage> Get(RelayTestHost relay, Enrolled worker, string path, CancellationToken ct = default)
        => relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, worker.WorkerId, path, worker.TunnelToken),
            HttpCompletionOption.ResponseHeadersRead, ct);

    [Fact]
    public async Task A_v01_single_frame_reply_arrives_whole()
    {
        await using Connected t = await ConnectedAsync();
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        byte[] image = FakeAgent.Payload(300 * 1024 + 17);
        Task<HttpResponseMessage> call = Get(relay, worker, "/view?filename=ComfyUI_00001_.png&type=output");

        var (request, body) = await agent.NextRequestAsync();
        Assert.Equal("GET", request.Method);
        Assert.Equal("/view?filename=ComfyUI_00001_.png&type=output", request.Path);
        Assert.Empty(body);
        // The relay's credential stops at the relay.
        Assert.DoesNotContain(request.Headers!.Keys, k => k.Equals("Authorization", StringComparison.OrdinalIgnoreCase));

        await agent.ReplyAsync(request.Id!, 200, image, "image/png");

        using HttpResponseMessage response = await call;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(image, await response.Content.ReadAsByteArrayAsync());
        await WaitBudgetEmptyAsync(relay);
    }

    [Fact]
    public async Task A_streamed_reply_arrives_whole()
    {
        await using Connected t = await ConnectedAsync();
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        byte[] video = FakeAgent.Payload(2 * WireCodec.MaxChunkBytes + 500_000);
        Task<HttpResponseMessage> call = Get(relay, worker, "/view?filename=clip.mp4&type=output");

        var (request, _) = await agent.NextRequestAsync();
        await agent.StreamAsync(request.Id!, 200, video);

        using HttpResponseMessage response = await call;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(video, await response.Content.ReadAsByteArrayAsync());
        await WaitBudgetEmptyAsync(relay);
    }

    [Fact]
    public async Task A_streamed_reply_outlives_the_timeout_while_it_keeps_moving()
    {
        // Timeout is silence, not size: three seconds of a reply trickling in
        // every half second is a busy node, and must not be cut off at one.
        await using Connected t = await ConnectedAsync(new Dictionary<string, string?> { ["Relay:TunnelTimeoutSeconds"] = "1" });
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        Task<HttpResponseMessage> call = Get(relay, worker, "/view?filename=slow.png&type=output");
        var (request, _) = await agent.NextRequestAsync();

        await agent.SendAsync(new TunnelHeader { Kind = FrameKind.ResponseHead, Id = request.Id, Status = 200 });
        var sent = new List<byte>();
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(500);
            byte[] piece = FakeAgent.Payload(1000, seed: i);
            sent.AddRange(piece);
            await agent.SendAsync(new TunnelHeader { Kind = FrameKind.ResponseChunk, Id = request.Id }, piece);
        }
        await agent.SendAsync(new TunnelHeader { Kind = FrameKind.ResponseEnd, Id = request.Id });

        using HttpResponseMessage response = await call;
        Assert.Equal(sent.ToArray(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_node_that_never_answers_times_out_as_504()
    {
        await using Connected t = await ConnectedAsync(new Dictionary<string, string?> { ["Relay:TunnelTimeoutSeconds"] = "1" });
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        using HttpResponseMessage response = await Get(relay, worker, "/queue");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);

        // And the node is told to stop working on it.
        var (request, _) = await agent.NextRequestAsync();
        var (cancel, _) = await agent.NextAsync();
        Assert.Equal(FrameKind.Cancel, cancel.Kind);
        Assert.Equal(request.Id, cancel.Id);
    }

    [Fact]
    public async Task A_reply_the_node_gives_up_on_is_cut_off_not_ended()
    {
        await using Connected t = await ConnectedAsync();
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        Task<HttpResponseMessage> call = Get(relay, worker, "/view?filename=half.png&type=output");
        var (request, _) = await agent.NextRequestAsync();
        await agent.StreamAsync(request.Id!, 200, FakeAgent.Payload(3 * 64 * 1024), chunkBytes: 64 * 1024, abortAfterFirstChunk: true);

        // Half an image must never read as a whole one.
        await AssertCutOffAsync(call);
    }

    [Fact]
    public async Task A_chunk_over_the_limit_kills_that_reply_and_nothing_else()
    {
        await using Connected t = await ConnectedAsync();
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        Task<HttpResponseMessage> call = Get(relay, worker, "/view?filename=big.png&type=output");
        var (request, _) = await agent.NextRequestAsync();
        await agent.SendAsync(new TunnelHeader { Kind = FrameKind.ResponseHead, Id = request.Id, Status = 200 });
        await agent.SendAsync(new TunnelHeader { Kind = FrameKind.ResponseChunk, Id = request.Id }, FakeAgent.Payload(WireCodec.MaxChunkBytes + 1));
        await agent.SendAsync(new TunnelHeader { Kind = FrameKind.ResponseEnd, Id = request.Id });

        await AssertCutOffAsync(call);

        // The node is told to stop sending it, and everything else carries on.
        await agent.WaitCancelledAsync(request.Id!);
        await AssertStillServesAsync(relay, worker, agent);
        await WaitBudgetEmptyAsync(relay);
    }

    [Fact]
    public async Task Frames_for_ids_nobody_is_waiting_for_are_dropped_as_they_arrive()
    {
        await using Connected t = await ConnectedAsync(new Dictionary<string, string?> { ["Relay:SessionBufferBytes"] = (2 * 1024 * 1024).ToString() });
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        // Far more than this session is allowed to buffer: if any of it were
        // kept, the budget would show it, and the next reply would not fit.
        await agent.ReplyAsync("no-such-request", 200, FakeAgent.Payload(8 * 1024 * 1024));
        await agent.SendAsync(new TunnelHeader { Kind = FrameKind.ResponseHead, Id = "also-unknown", Status = 200 });
        for (int i = 0; i < 4; i++)
            await agent.SendAsync(new TunnelHeader { Kind = FrameKind.ResponseChunk, Id = "also-unknown" }, FakeAgent.Payload(WireCodec.MaxChunkBytes));
        await agent.SendAsync(new TunnelHeader { Kind = FrameKind.ResponseEnd, Id = "also-unknown" });

        // The agent's frames are read in order, so once this answer is through,
        // everything before it has been dealt with.
        await AssertStillServesAsync(relay, worker, agent);
        await WaitBudgetEmptyAsync(relay);
    }

    [Fact]
    public async Task Frames_the_relay_cannot_read_are_skipped_without_ending_the_session()
    {
        await using Connected t = await ConnectedAsync();
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        // Wrong version.
        await agent.SendRawAsync(new byte[] { 9, 0, 0, 0, 2, (byte)'{', (byte)'}' }, endOfMessage: true);
        // A header that claims to be larger than any header may be.
        var huge = new byte[WireCodec.PrefixBytes + 16];
        huge[0] = WireCodec.Version;
        BinaryPrimitives.WriteInt32BigEndian(huge.AsSpan(1), WireCodec.MaxHeaderBytes + 1);
        await agent.SendRawAsync(huge, endOfMessage: true);
        // Not JSON.
        byte[] junk = Encoding.UTF8.GetBytes("not json at all");
        var badJson = new byte[WireCodec.PrefixBytes + junk.Length];
        badJson[0] = WireCodec.Version;
        BinaryPrimitives.WriteInt32BigEndian(badJson.AsSpan(1), junk.Length);
        junk.CopyTo(badJson, WireCodec.PrefixBytes);
        await agent.SendRawAsync(badJson, endOfMessage: true);
        // A frame split across fragments mid-header, then ended too soon.
        await agent.SendRawAsync(new byte[] { WireCodec.Version, 0 }, endOfMessage: false);
        await agent.SendRawAsync(new byte[] { 0, 0 }, endOfMessage: true);

        await AssertStillServesAsync(relay, worker, agent);
    }

    [Fact]
    public async Task A_reply_split_across_fragments_mid_header_still_arrives()
    {
        await using Connected t = await ConnectedAsync();
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        Task<HttpResponseMessage> call = Get(relay, worker, "/queue");
        var (request, _) = await agent.NextRequestAsync();

        byte[] frame = WireCodec.Encode(new TunnelHeader
        {
            Kind = FrameKind.Response,
            Id = request.Id,
            Status = 200,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
        }, """{"queue_running":[]}"""u8);
        for (int offset = 0; offset < frame.Length; offset += 3)
            await agent.SendRawAsync(frame.AsMemory(offset, Math.Min(3, frame.Length - offset)), endOfMessage: offset + 3 >= frame.Length);

        using HttpResponseMessage response = await call;
        Assert.Equal("""{"queue_running":[]}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task When_aixman_hangs_up_the_node_is_told_and_its_late_answer_dropped()
    {
        await using Connected t = await ConnectedAsync();
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        using var hangUp = new CancellationTokenSource();
        Task<HttpResponseMessage> call = Get(relay, worker, "/view?filename=x.png&type=output", hangUp.Token);
        var (request, _) = await agent.NextRequestAsync();

        await hangUp.CancelAsync();
        try
        {
            (await call).Dispose();
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            // How the hang-up surfaces on the client side is the client's business.
        }

        var (cancel, _) = await agent.NextAsync();
        Assert.Equal(FrameKind.Cancel, cancel.Kind);
        Assert.Equal(request.Id, cancel.Id);

        await agent.ReplyAsync(request.Id!, 200, FakeAgent.Payload(1024 * 1024));
        await AssertStillServesAsync(relay, worker, agent);
        await WaitBudgetEmptyAsync(relay);
    }

    [Fact]
    public async Task A_node_that_drops_mid_request_answers_503_offline()
    {
        await using Connected t = await ConnectedAsync();
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        Task<HttpResponseMessage> call = Get(relay, worker, "/queue");
        await agent.NextRequestAsync();
        await agent.DisposeAsync();

        using HttpResponseMessage response = await call;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("offline", body.RootElement.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Paths_off_the_allowlist_never_reach_the_node()
    {
        await using Connected t = await ConnectedAsync();
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/customnode/install"),
                     (HttpMethod.Get, "/userdata?dir=workflows"),
                     (HttpMethod.Post, "/free"),
                     (HttpMethod.Get, "/object_info/..%2F..%2Fuserdata"),
                     (HttpMethod.Get, "/view?filename=..%2F..%2Fsecret.txt"),
                 })
        {
            using HttpResponseMessage response = await relay.Http.SendAsync(
                RelayTestHost.Tunnel(method, worker.WorkerId, path, worker.TunnelToken, method == HttpMethod.Post ? new StringContent("{}") : null));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains(TunnelAllowlist.DeniedError, await response.Content.ReadAsStringAsync());
        }

        // Clearing the owner's history is refused by its body...
        using (HttpResponseMessage clear = await relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Post, worker.WorkerId, "/history",
                   worker.TunnelToken, new StringContent("""{"clear":true}""", Encoding.UTF8, "application/json"))))
            Assert.Equal(HttpStatusCode.Forbidden, clear.StatusCode);

        Assert.True(await agent.QuietForAsync(TimeSpan.FromMilliseconds(300)));

        // ...while deleting aixman's own prompt goes through, body intact.
        const string delete = """{"delete":["0f8c4f7e-3b1a-4c2e-9d3b-6b1f0a2c9e11"]}""";
        Task<HttpResponseMessage> call = relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Post, worker.WorkerId, "/history",
            worker.TunnelToken, new StringContent(delete, Encoding.UTF8, "application/json")));
        var (request, body) = await agent.NextRequestAsync();
        Assert.Equal("POST", request.Method);
        Assert.Equal(delete, Encoding.UTF8.GetString(body));
        await agent.ReplyAsync(request.Id!, 200, []);
        using HttpResponseMessage ok = await call;
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task An_upload_reaches_the_node_byte_for_byte_and_an_oversized_one_is_refused()
    {
        await using Connected t = await ConnectedAsync(new Dictionary<string, string?> { ["Relay:MaxRequestBodyBytes"] = (512 * 1024).ToString() });
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        byte[] upload = FakeAgent.Payload(300 * 1024, seed: 3);
        Task<HttpResponseMessage> call = relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Post, worker.WorkerId, "/upload/image",
            worker.TunnelToken, new ByteArrayContent(upload)));
        var (request, body) = await agent.NextRequestAsync();
        Assert.Equal(upload, body);
        await agent.ReplyAsync(request.Id!, 200, """{"name":"x.png"}"""u8.ToArray(), "application/json");
        using (HttpResponseMessage response = await call)
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using HttpResponseMessage tooBig = await relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Post, worker.WorkerId, "/upload/image",
            worker.TunnelToken, new ByteArrayContent(FakeAgent.Payload(600 * 1024))));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.True(await agent.QuietForAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(0, relay.Service<RelayBudget>().Budget.Used);
    }

    [Fact]
    public async Task Tunnel_requests_past_the_per_worker_rate_are_429()
    {
        await using Connected t = await ConnectedAsync(new Dictionary<string, string?> { ["Relay:TunnelRequestsPerMinutePerWorker"] = "5" });
        var (relay, worker, agent) = (t.Relay, t.Worker, t.Agent);

        // Refused before they reach the node, so no replies are needed: the
        // allowlist 403 still counts against the rate.
        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 4; i++)
        {
            using HttpResponseMessage response = await relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, worker.WorkerId, "/not-allowed", worker.TunnelToken));
            statuses.Add(response.StatusCode);
        }
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    /// <summary>The session is alive and routing: one more request, one more answer.</summary>
    private static async Task AssertStillServesAsync(RelayTestHost relay, Enrolled worker, FakeAgent agent)
    {
        Task<HttpResponseMessage> call = Get(relay, worker, "/aixman/ready");
        var (request, _) = await agent.NextRequestAsync(skipCancels: true);
        Assert.Equal("/aixman/ready", request.Path);
        await agent.ReplyAsync(request.Id!, 200, """{"ready":true}"""u8.ToArray(), "application/json");
        using HttpResponseMessage response = await call;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"ready":true}""", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Budgets are released by the handler as it writes; give it a moment to finish.</summary>
    private static async Task WaitBudgetEmptyAsync(RelayTestHost relay)
    {
        var budget = relay.Service<RelayBudget>().Budget;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (budget.Used != 0)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"relay still holds {budget.Used} bytes");
            await Task.Delay(10);
        }
    }
}
