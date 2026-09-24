using System.Net.WebSockets;
using System.Threading.Channels;
using GpuxMine.Protocol;

namespace GpuxMine.Relay.Tests;

/// <summary>
/// An agent written frame by frame, so a test can send exactly what an old
/// v0.1 agent sends, what a current one sends, and what a hostile one might.
/// </summary>
public sealed class FakeAgent : IAsyncDisposable
{
    private readonly Channel<(TunnelHeader Header, byte[] Body)> _frames = Channel.CreateUnbounded<(TunnelHeader, byte[])>();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly Task _pump;

    private FakeAgent(WebSocket socket)
    {
        Socket = socket;
        _pump = PumpAsync();
    }

    public WebSocket Socket { get; }
    public TunnelHeader HelloAck { get; private set; } = new();

    /// <summary>Every id the relay has sent a cancel for, in whatever order it arrived.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, bool> CancelledIds { get; } = new();

    public static async Task<FakeAgent> ConnectAsync(RelayTestHost host, string workerId, string token, string version)
    {
        var client = host.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["X-Worker-Id"] = workerId;
            request.Headers.Authorization = "Bearer " + token;
            request.Headers["X-Agent-Version"] = version;
        };

        WebSocket socket = await client.ConnectAsync(new Uri("ws://localhost/agent"), CancellationToken.None);
        var agent = new FakeAgent(socket);
        agent.HelloAck = (await agent.NextAsync()).Header;
        Assert.Equal(FrameKind.HelloAck, agent.HelloAck.Kind);
        return agent;
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[64 * 1024];
        using var assembled = new MemoryStream();
        try
        {
            while (Socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await Socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    assembled.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                (TunnelHeader header, byte[] body) = WireCodec.Decode(assembled.ToArray());
                assembled.SetLength(0);
                if (header.Kind == FrameKind.Cancel && header.Id is not null) CancelledIds[header.Id] = true;
                await _frames.Writer.WriteAsync((header, body));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The relay went away.
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    /// <summary>The next frame from the relay, or a timeout.</summary>
    public async Task<(TunnelHeader Header, byte[] Body)> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        return await _frames.Reader.ReadAsync(cts.Token);
    }

    /// <summary>
    /// The next request. A cancel in between fails the test unless
    /// <paramref name="skipCancels"/> says one may come — the relay sends
    /// cancels without waiting, so after an aborted reply one can arrive
    /// before or after the next request.
    /// </summary>
    public async Task<(TunnelHeader Header, byte[] Body)> NextRequestAsync(TimeSpan? timeout = null, bool skipCancels = false)
    {
        while (true)
        {
            var frame = await NextAsync(timeout);
            if (skipCancels && frame.Header.Kind == FrameKind.Cancel) continue;
            Assert.Equal(FrameKind.Request, frame.Header.Kind);
            return frame;
        }
    }

    /// <summary>Waits for the relay to cancel <paramref name="id"/>.</summary>
    public async Task WaitCancelledAsync(string id, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!CancelledIds.ContainsKey(id))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"the relay never cancelled {id}");
            await Task.Delay(10);
        }
    }

    /// <summary>True when nothing arrives within <paramref name="wait"/>.</summary>
    public async Task<bool> QuietForAsync(TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        try
        {
            return !await _frames.Reader.WaitToReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>Completes when the relay has closed the socket.</summary>
    public async Task WaitClosedAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (await _frames.Reader.WaitToReadAsync(cts.Token))
        {
            _frames.Reader.TryRead(out _);
        }
    }

    public async Task SendAsync(TunnelHeader header, ReadOnlyMemory<byte> body = default)
        => await SendRawAsync(WireCodec.Encode(header, body.Span), endOfMessage: true);

    public async Task SendRawAsync(ReadOnlyMemory<byte> bytes, bool endOfMessage)
    {
        await _send.WaitAsync();
        try
        {
            await Socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage, CancellationToken.None);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>A v0.1 reply: status, headers and all of the body in one frame.</summary>
    public Task ReplyAsync(string id, int status, byte[] body, string contentType = "application/octet-stream")
        => SendAsync(new TunnelHeader
        {
            Kind = FrameKind.Response,
            Id = id,
            Status = status,
            Headers = new Dictionary<string, string> { ["Content-Type"] = contentType },
        }, body);

    /// <summary>A current reply: head, chunks, end.</summary>
    public async Task StreamAsync(string id, int status, byte[] body, int chunkBytes = WireCodec.MaxChunkBytes, bool abortAfterFirstChunk = false)
    {
        await SendAsync(new TunnelHeader
        {
            Kind = FrameKind.ResponseHead,
            Id = id,
            Status = status,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/octet-stream" },
        });

        for (int offset = 0; offset < body.Length; offset += chunkBytes)
        {
            int length = Math.Min(chunkBytes, body.Length - offset);
            await SendAsync(new TunnelHeader { Kind = FrameKind.ResponseChunk, Id = id }, body.AsMemory(offset, length));
            if (abortAfterFirstChunk)
            {
                await SendAsync(new TunnelHeader { Kind = FrameKind.ResponseEnd, Id = id, Aborted = true });
                return;
            }
        }

        await SendAsync(new TunnelHeader { Kind = FrameKind.ResponseEnd, Id = id });
    }

    public Task HeartbeatAsync(AgentTelemetry telemetry)
        => SendAsync(new TunnelHeader { Kind = FrameKind.Heartbeat, Telemetry = telemetry });

    public static byte[] Payload(int length, int seed = 7)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test done", cts.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
        Socket.Abort();
        Socket.Dispose();
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
    }
}
