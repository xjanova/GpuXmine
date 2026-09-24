using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GpuxMine.Protocol;

namespace GpuxMine.Relay.Tests;

public class RelayAdminTests
{
    [Fact]
    public async Task Enrolment_needs_the_admin_key_and_returns_both_tokens()
    {
        await using var relay = await RelayTestHost.StartAsync();

        using (HttpResponseMessage anonymous = await relay.Http.PostAsync("/enroll?label=x", null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (HttpResponseMessage observer = await relay.AdminAsync(HttpMethod.Post, "/enroll?label=x", RelayTestHost.ObserverKey))
            Assert.Equal(HttpStatusCode.Unauthorized, observer.StatusCode);

        Enrolled enrolled = await relay.EnrollAsync();
        Assert.StartsWith("gxm-", enrolled.WorkerId);
        Assert.False(string.IsNullOrEmpty(enrolled.Token));
        Assert.False(string.IsNullOrEmpty(enrolled.TunnelToken));
        Assert.NotEqual(enrolled.Token, enrolled.TunnelToken);
        Assert.EndsWith($"/w/{enrolled.WorkerId}", enrolled.AixmanEndpoint);
        Assert.EndsWith("/agent", enrolled.AgentRelayUrl);
    }

    [Fact]
    public async Task Until_tunnel_tokens_are_switched_on_enrolment_stays_one_token_for_both_doors()
    {
        // What an XMAN Studio build from before the split needs: it pushes the
        // node's token to aixman, and that token must open the tunnel.
        await using var relay = await RelayTestHost.StartAsync(new Dictionary<string, string?> { ["Relay:IssueTunnelTokens"] = "false" });

        Enrolled worker = await relay.EnrollAsync();
        Assert.Equal(worker.Token, worker.TunnelToken);

        await using FakeAgent agent = await relay.ConnectAgentAsync(worker.WorkerId, worker.Token!);
        await relay.WaitOnlineAsync(worker.WorkerId);
        Task<HttpResponseMessage> call = relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, worker.WorkerId, "/queue", worker.Token));
        var (request, _) = await agent.NextRequestAsync();
        await agent.ReplyAsync(request.Id!, 200, "{}"u8.ToArray(), "application/json");
        using (HttpResponseMessage response = await call)
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement entry = await WorkerEntryAsync(relay, worker.WorkerId);
        Assert.False(entry.GetProperty("hasTunnelToken").GetBoolean());
    }

    [Fact]
    public async Task The_tunnel_takes_the_tunnel_token_and_not_the_agent_token()
    {
        await using var relay = await RelayTestHost.StartAsync();
        Enrolled worker = await relay.EnrollAsync();

        using (var withAgentToken = await relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, worker.WorkerId, "/queue", worker.Token)))
            Assert.Equal(HttpStatusCode.Unauthorized, withAgentToken.StatusCode);
        using (var noToken = await relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, worker.WorkerId, "/queue", null)))
            Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        // Right token, node not connected: the "warming" answer, not a failure.
        using var offline = await relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, worker.WorkerId, "/queue", worker.TunnelToken));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await offline.Content.ReadAsStringAsync());
        Assert.Equal("offline", body.RootElement.GetProperty("stage").GetString());
        Assert.False(body.RootElement.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task The_agent_door_takes_the_agent_token_and_not_the_tunnel_token()
    {
        await using var relay = await RelayTestHost.StartAsync();
        Enrolled worker = await relay.EnrollAsync();

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => relay.ConnectAgentAsync(worker.WorkerId, worker.TunnelToken));
        Assert.Contains("401", refused.Message);

        await using FakeAgent agent = await relay.ConnectAgentAsync(worker.WorkerId, worker.Token!);
        Assert.Contains(TunnelCaps.ResponseStreaming, agent.HelloAck.Caps ?? []);
        Assert.Equal(WireCodec.MaxChunkBytes, agent.HelloAck.MaxChunkBytes);
    }

    [Fact]
    public async Task A_legacy_worker_keeps_working_and_can_be_moved_to_its_own_tunnel_token()
    {
        await using var relay = await RelayTestHost.StartAsync(storeJson: WorkerStoreTests.LegacyStore("gxm-legacy01", "old-token"));

        await using FakeAgent agent = await relay.ConnectAgentAsync("gxm-legacy01", "old-token");
        await relay.WaitOnlineAsync("gxm-legacy01");

        // Before: aixman holds the one token, and it still opens the tunnel.
        Task<HttpResponseMessage> call = relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, "gxm-legacy01", "/queue", "old-token"));
        var (request, _) = await agent.NextRequestAsync();
        await agent.ReplyAsync(request.Id!, 200, "{}"u8.ToArray(), "application/json");
        using (HttpResponseMessage response = await call)
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Give it a tunnel token of its own, without cutting the node off.
        using HttpResponseMessage rotated = await relay.AdminAsync(HttpMethod.Post, "/admin/workers/gxm-legacy01/rotate?only=tunnel");
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Enrolled issued = (await rotated.Content.ReadFromJsonAsync<Enrolled>(RelayTestHost.Json))!;
        Assert.Null(issued.Token);
        Assert.False(string.IsNullOrEmpty(issued.TunnelToken));

        Assert.NotNull(relay.Service<AgentRegistry>().Get("gxm-legacy01"));

        using (var old = await relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, "gxm-legacy01", "/queue", "old-token")))
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);

        call = relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, "gxm-legacy01", "/queue", issued.TunnelToken));
        (request, _) = await agent.NextRequestAsync();
        await agent.ReplyAsync(request.Id!, 200, "{}"u8.ToArray(), "application/json");
        using (HttpResponseMessage response = await call)
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_disabled_worker_is_cut_off_on_both_doors_until_enabled()
    {
        await using var relay = await RelayTestHost.StartAsync();
        Enrolled worker = await relay.EnrollAsync();
        await using FakeAgent agent = await relay.ConnectAgentAsync(worker.WorkerId, worker.Token!);
        await relay.WaitOnlineAsync(worker.WorkerId);

        using (HttpResponseMessage disabled = await relay.AdminAsync(HttpMethod.Post, $"/admin/workers/{worker.WorkerId}/disable?reason=chargeback"))
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        // The live session is closed...
        await agent.WaitClosedAsync();
        await relay.WaitOnlineAsync(worker.WorkerId, online: false);

        // ...the node is told 403, not 401, when it knocks again...
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => relay.ConnectAgentAsync(worker.WorkerId, worker.Token!));
        Assert.Contains("403", refused.Message);

        // ...and aixman gets 403 too.
        using (var tunnel = await relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, worker.WorkerId, "/queue", worker.TunnelToken)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, tunnel.StatusCode);
            Assert.Contains("worker-disabled", await tunnel.Content.ReadAsStringAsync());
        }

        JsonElement listed = await WorkerEntryAsync(relay, worker.WorkerId);
        Assert.True(listed.GetProperty("disabled").GetBoolean());
        Assert.Equal("chargeback", listed.GetProperty("disabledReason").GetString());

        using (HttpResponseMessage enabled = await relay.AdminAsync(HttpMethod.Post, $"/admin/workers/{worker.WorkerId}/enable"))
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);

        await using FakeAgent back = await relay.ConnectAgentAsync(worker.WorkerId, worker.Token!);
        await relay.WaitOnlineAsync(worker.WorkerId);
    }

    [Fact]
    public async Task Deleting_a_worker_closes_it_and_is_idempotent()
    {
        await using var relay = await RelayTestHost.StartAsync();
        Enrolled worker = await relay.EnrollAsync();
        await using FakeAgent agent = await relay.ConnectAgentAsync(worker.WorkerId, worker.Token!);
        await relay.WaitOnlineAsync(worker.WorkerId);

        using (HttpResponseMessage first = await relay.AdminAsync(HttpMethod.Delete, $"/admin/workers/{worker.WorkerId}"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("deleted").GetBoolean());
            Assert.True(body.RootElement.GetProperty("existed").GetBoolean());
        }

        await agent.WaitClosedAsync();

        using (HttpResponseMessage again = await relay.AdminAsync(HttpMethod.Delete, $"/admin/workers/{worker.WorkerId}"))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("deleted").GetBoolean());
            Assert.False(body.RootElement.GetProperty("existed").GetBoolean());
        }

        using (var tunnel = await relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, worker.WorkerId, "/queue", worker.TunnelToken)))
            Assert.Equal(HttpStatusCode.Unauthorized, tunnel.StatusCode);
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => relay.ConnectAgentAsync(worker.WorkerId, worker.Token!));
        Assert.Contains("401", refused.Message);
    }

    [Fact]
    public async Task Full_rotation_closes_the_session_and_only_the_new_tokens_work()
    {
        await using var relay = await RelayTestHost.StartAsync();
        Enrolled worker = await relay.EnrollAsync();
        await using FakeAgent agent = await relay.ConnectAgentAsync(worker.WorkerId, worker.Token!);
        await relay.WaitOnlineAsync(worker.WorkerId);

        using HttpResponseMessage rotated = await relay.AdminAsync(HttpMethod.Post, $"/admin/workers/{worker.WorkerId}/rotate");
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Enrolled fresh = (await rotated.Content.ReadFromJsonAsync<Enrolled>(RelayTestHost.Json))!;
        Assert.Equal(worker.WorkerId, fresh.WorkerId);
        Assert.NotEqual(worker.Token, fresh.Token);
        Assert.NotEqual(worker.TunnelToken, fresh.TunnelToken);

        await agent.WaitClosedAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => relay.ConnectAgentAsync(worker.WorkerId, worker.Token!));
        using (var old = await relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, worker.WorkerId, "/queue", worker.TunnelToken)))
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);

        await using FakeAgent back = await relay.ConnectAgentAsync(worker.WorkerId, fresh.Token!);
        await relay.WaitOnlineAsync(worker.WorkerId);
    }

    [Fact]
    public async Task Admin_actions_on_an_unknown_worker_are_404_and_bad_rotate_options_400()
    {
        await using var relay = await RelayTestHost.StartAsync();
        Enrolled worker = await relay.EnrollAsync();

        foreach (string action in new[] { "disable", "enable", "rotate" })
        {
            using HttpResponseMessage response = await relay.AdminAsync(HttpMethod.Post, $"/admin/workers/gxm-000000000000/{action}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using HttpResponseMessage bad = await relay.AdminAsync(HttpMethod.Post, $"/admin/workers/{worker.WorkerId}/rotate?only=agent");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task The_observer_key_can_list_workers_and_do_nothing_else()
    {
        await using var relay = await RelayTestHost.StartAsync();
        Enrolled worker = await relay.EnrollAsync();

        using (HttpResponseMessage asHeader = await relay.AdminAsync(HttpMethod.Get, "/admin/workers", RelayTestHost.ObserverKey, "X-Observer-Key"))
            Assert.Equal(HttpStatusCode.OK, asHeader.StatusCode);
        // In the admin header too, so a caller can switch keys without a code change.
        using (HttpResponseMessage inAdminHeader = await relay.AdminAsync(HttpMethod.Get, "/admin/workers", RelayTestHost.ObserverKey))
            Assert.Equal(HttpStatusCode.OK, inAdminHeader.StatusCode);
        using (HttpResponseMessage none = await relay.Http.GetAsync("/admin/workers"))
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, $"/admin/workers/{worker.WorkerId}/disable"),
                     (HttpMethod.Post, $"/admin/workers/{worker.WorkerId}/rotate"),
                     (HttpMethod.Delete, $"/admin/workers/{worker.WorkerId}"),
                 })
        {
            using HttpResponseMessage response = await relay.AdminAsync(method, path, RelayTestHost.ObserverKey);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.NotNull(relay.Service<WorkerStore>().Find(worker.WorkerId));
        Assert.False(relay.Service<WorkerStore>().Find(worker.WorkerId)!.Disabled);
    }

    [Fact]
    public async Task Worker_list_carries_busy_accepting_and_last_seen()
    {
        await using var relay = await RelayTestHost.StartAsync();
        Enrolled worker = await relay.EnrollAsync();

        // Never connected: nothing known.
        JsonElement entry = await WorkerEntryAsync(relay, worker.WorkerId);
        Assert.False(entry.GetProperty("online").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("busy").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("accepting").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("lastSeenAt").ValueKind);
        Assert.True(entry.GetProperty("hasTunnelToken").GetBoolean());

        await using (FakeAgent agent = await relay.ConnectAgentAsync(worker.WorkerId, worker.Token!))
        {
            await relay.WaitOnlineAsync(worker.WorkerId);

            // An old agent's heartbeat: accepting, but no word on busy.
            await agent.HeartbeatAsync(new AgentTelemetry { Accepting = true, GpuName = "GTX 1070 Ti" });
            entry = await PollEntryAsync(relay, worker.WorkerId, e => e.GetProperty("accepting").ValueKind == JsonValueKind.True);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("busy").ValueKind);
            Assert.Equal("GTX 1070 Ti", entry.GetProperty("telemetry").GetProperty("gpuName").GetString());

            // A current agent's: busy, and paused.
            await agent.HeartbeatAsync(new AgentTelemetry { Accepting = false, Busy = true, QueueRemaining = 2 });
            entry = await PollEntryAsync(relay, worker.WorkerId, e => e.GetProperty("busy").ValueKind == JsonValueKind.True);
            Assert.False(entry.GetProperty("accepting").GetBoolean());
            Assert.Equal(2, entry.GetProperty("telemetry").GetProperty("queueRemaining").GetInt32());
        }

        // Gone, but remembered.
        await relay.WaitOnlineAsync(worker.WorkerId, online: false);
        entry = await WorkerEntryAsync(relay, worker.WorkerId);
        Assert.False(entry.GetProperty("online").GetBoolean());
        Assert.Equal(JsonValueKind.String, entry.GetProperty("lastSeenAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("busy").ValueKind);
    }

    [Fact]
    public async Task A_worker_reconnecting_in_a_loop_is_rate_limited()
    {
        await using var relay = await RelayTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Relay:AgentConnectsPerMinutePerWorker"] = "2",
        });
        Enrolled worker = await relay.EnrollAsync();

        await using (await relay.ConnectAgentAsync(worker.WorkerId, worker.Token!)) { }
        await using (await relay.ConnectAgentAsync(worker.WorkerId, worker.Token!)) { }

        var limited = await Assert.ThrowsAnyAsync<Exception>(() => relay.ConnectAgentAsync(worker.WorkerId, worker.Token!));
        Assert.Contains("429", limited.Message);
    }

    [Fact]
    public async Task A_silent_session_is_swept()
    {
        await using var relay = await RelayTestHost.StartAsync();
        Enrolled worker = await relay.EnrollAsync();
        await using FakeAgent agent = await relay.ConnectAgentAsync(worker.WorkerId, worker.Token!);
        await relay.WaitOnlineAsync(worker.WorkerId);

        var sweeper = new SessionSweeper(relay.Service<AgentRegistry>(), relay.Service<RelayOptions>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionSweeper>.Instance);

        Assert.Equal(0, await sweeper.SweepAsync(TimeSpan.FromMinutes(5)));
        await Task.Delay(50);
        Assert.Equal(1, await sweeper.SweepAsync(TimeSpan.FromMilliseconds(10)));

        await agent.WaitClosedAsync();
        Assert.Null(relay.Service<AgentRegistry>().Get(worker.WorkerId));
    }

    private static async Task<JsonElement> WorkerEntryAsync(RelayTestHost relay, string workerId)
    {
        using HttpResponseMessage response = await relay.AdminAsync(HttpMethod.Get, "/admin/workers");
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray().Single(e => e.GetProperty("workerId").GetString() == workerId).Clone();
    }

    private static async Task<JsonElement> PollEntryAsync(RelayTestHost relay, string workerId, Func<JsonElement, bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            JsonElement entry = await WorkerEntryAsync(relay, workerId);
            if (until(entry)) return entry;
            if (DateTime.UtcNow > deadline) throw new TimeoutException(entry.ToString());
            await Task.Delay(20);
        }
    }
}
