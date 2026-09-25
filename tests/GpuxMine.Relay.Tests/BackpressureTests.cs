using System.Net;
using System.Net.WebSockets;
using GpuxMine.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace GpuxMine.Relay.Tests;

/// <summary>
/// A node that stops reading its socket, and uploads piling up behind it: what
/// the relay holds for that node stays that node's, and does not last.
/// </summary>
public class BackpressureTests
{
    /// <summary>A socket whose peer never reads: every write waits until it is cancelled.</summary>
    private sealed class StalledSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => _state = WebSocketState.Closed;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    [Fact]
    public async Task A_node_that_stops_reading_loses_its_session_instead_of_holding_what_it_was_sent()
    {
        var options = new RelayOptions { SendStallSeconds = 1 };
        var session = new AgentSession("gxm-stalled", "9.9.9", null, new StalledSocket(), options, new ByteBudget(64L * 1024 * 1024), NullLogger.Instance);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Task<TunnelExchange> upload = session.OpenExchangeAsync("POST", "/upload/image", headers, FakeAgent.Payload(3 * 1024 * 1024), CancellationToken.None);
        // Queued behind it, waiting for the write gate.
        Task<TunnelExchange> probe = session.OpenExchangeAsync("GET", "/aixman/ready", headers, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        await Assert.ThrowsAsync<TunnelOfflineException>(() => upload.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAnyAsync<Exception>(() => probe.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8));
        Assert.True(session.Closed.IsCancellationRequested);
        Assert.Equal(0, session.InFlight);
    }

    [Fact]
    public async Task One_nodes_backlog_of_uploads_does_not_crowd_out_another_nodes()
    {
        await using var relay = await RelayTestHost.StartAsync();
        Enrolled slow = await relay.EnrollAsync("slow");
        Enrolled other = await relay.EnrollAsync("other");
        await using FakeAgent slowAgent = await relay.ConnectAgentAsync(slow.WorkerId, slow.Token!);
        await using FakeAgent otherAgent = await relay.ConnectAgentAsync(other.WorkerId, other.Token!);
        await relay.WaitOnlineAsync(slow.WorkerId);
        await relay.WaitOnlineAsync(other.WorkerId);

        // Everything the slow node may hold is taken by uploads it has not read yet.
        AgentSession slowSession = relay.Service<AgentRegistry>().Get(slow.WorkerId)!;
        Assert.True(slowSession.RequestBudget.TryReserve(slowSession.RequestBudget.Capacity));

        using (HttpResponseMessage refused = await relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Post, slow.WorkerId, "/upload/image",
                   slow.TunnelToken, new ByteArrayContent(FakeAgent.Payload(64 * 1024)))))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Contains("relay-busy", await refused.Content.ReadAsStringAsync());
        }

        // The other node's upload goes through regardless.
        byte[] upload = FakeAgent.Payload(64 * 1024, seed: 5);
        Task<HttpResponseMessage> call = relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Post, other.WorkerId, "/upload/image",
            other.TunnelToken, new ByteArrayContent(upload)));
        var (request, body) = await otherAgent.NextRequestAsync();
        Assert.Equal(upload, body);
        await otherAgent.ReplyAsync(request.Id!, 200, """{"name":"aixman-first-1.png"}"""u8.ToArray(), "application/json");
        using (HttpResponseMessage response = await call)
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        slowSession.RequestBudget.Release(slowSession.RequestBudget.Capacity);
        RelayBudget budgets = relay.Service<RelayBudget>();
        Assert.Equal(0, budgets.Requests.Used);
        Assert.Equal(0, slowSession.RequestBudget.Used);
    }

    [Fact]
    public async Task Replies_still_get_through_when_uploads_have_taken_their_whole_share()
    {
        await using var relay = await RelayTestHost.StartAsync();
        Enrolled worker = await relay.EnrollAsync();
        await using FakeAgent agent = await relay.ConnectAgentAsync(worker.WorkerId, worker.Token!);
        await relay.WaitOnlineAsync(worker.WorkerId);

        RelayBudget budgets = relay.Service<RelayBudget>();
        Assert.True(budgets.Requests.Capacity < budgets.Budget.Capacity);
        Assert.True(budgets.Requests.TryReserve(budgets.Requests.Capacity));
        Assert.True(budgets.Budget.TryReserve(budgets.Requests.Capacity));   // held relay-wide too, as a real upload would be

        using (HttpResponseMessage refused = await relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Post, worker.WorkerId, "/upload/image",
                   worker.TunnelToken, new ByteArrayContent(FakeAgent.Payload(1024)))))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

        // A finished render's file on its way back to aixman still has room.
        byte[] image = FakeAgent.Payload(2 * 1024 * 1024, seed: 9);
        Task<HttpResponseMessage> call = relay.Http.SendAsync(RelayTestHost.Tunnel(HttpMethod.Get, worker.WorkerId,
            "/view?filename=aix_00001_.png&type=output", worker.TunnelToken));
        var (request, _) = await agent.NextRequestAsync();
        await agent.StreamAsync(request.Id!, 200, image, chunkBytes: 256 * 1024);
        using HttpResponseMessage response = await call;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(image, await response.Content.ReadAsByteArrayAsync());

        budgets.Requests.Release(budgets.Requests.Capacity);
        budgets.Budget.Release(budgets.Requests.Capacity);
    }
}
