using System.Collections.Concurrent;
using System.Net.WebSockets;
using GpuxMine.Protocol;

namespace GpuxMine.Relay;

public sealed record TunnelReply(int Status, Dictionary<string, string> Headers, byte[] Body);

/// <summary>
/// One connected agent. Owns the socket for its whole life: every write goes
/// through <see cref="_writeGate"/> because a WebSocket permits exactly one
/// send at a time, and aixman happily issues concurrent requests (a health
/// probe while a render is being polled).
/// </summary>
public sealed class AgentSession : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TunnelReply>> _pending = new();
    private readonly CancellationTokenSource _closed = new();

    public string WorkerId { get; }
    public string? AgentVersion { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; private set; } = DateTimeOffset.UtcNow;
    public AgentTelemetry? Telemetry { get; private set; }
    public CancellationToken Closed => _closed.Token;

    public AgentSession(string workerId, string? agentVersion, WebSocket socket, ILogger log)
    {
        WorkerId = workerId;
        AgentVersion = agentVersion;
        _socket = socket;
        _log = log;
    }

    /// <summary>
    /// Pumps frames until the socket dies. Returns only when the session is over;
    /// the caller keeps the HTTP request alive for exactly that long.
    /// </summary>
    public async Task RunAsync(CancellationToken hostStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStopping, _closed.Token);
        var buffer = new byte[64 * 1024];
        var assembled = new MemoryStream();

        try
        {
            while (_socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, linked.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogInformation("Agent {WorkerId} closed the socket: {Status} {Description}",
                            WorkerId, result.CloseStatus, result.CloseStatusDescription);
                        return;
                    }

                    if (assembled.Length + result.Count > WireCodec.MaxFrameBytes)
                        throw new InvalidDataException($"Agent {WorkerId} sent a frame over the {WireCodec.MaxFrameBytes} byte ceiling");

                    assembled.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                byte[] frame = assembled.ToArray();
                assembled.SetLength(0);
                LastSeenAt = DateTimeOffset.UtcNow;
                Dispatch(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down, or Dispose asked us to stop. Not an error.
        }
        catch (WebSocketException ex)
        {
            _log.LogInformation("Agent {WorkerId} dropped: {Message}", WorkerId, ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Agent {WorkerId} session failed", WorkerId);
        }
    }

    private void Dispatch(byte[] frame)
    {
        TunnelHeader header;
        byte[] body;
        try
        {
            (header, body) = WireCodec.Decode(frame);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Agent {WorkerId} sent an undecodable frame: {Message}", WorkerId, ex.Message);
            return;
        }

        switch (header.Kind)
        {
            case FrameKind.Response:
                if (header.Id is null) return;
                if (_pending.TryRemove(header.Id, out var waiter))
                {
                    waiter.TrySetResult(new TunnelReply(
                        header.Status ?? 502,
                        header.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        body));
                }
                break;

            case FrameKind.Heartbeat:
                Telemetry = header.Telemetry;
                break;

            case FrameKind.Bye:
                _log.LogInformation("Agent {WorkerId} said goodbye: {Reason}", WorkerId, header.Reason);
                _closed.Cancel();
                break;
        }
    }

    public async Task SendAsync(TunnelHeader header, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        byte[] frame = WireCodec.Encode(header, body.Span);
        await _writeGate.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Sends an HTTP request down the tunnel and waits for the matching reply.
    /// </summary>
    public async Task<TunnelReply> ExchangeAsync(
        string method,
        string pathAndQuery,
        Dictionary<string, string> headers,
        byte[] body,
        TimeSpan timeout,
        CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString("n");
        var waiter = new TaskCompletionSource<TunnelReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        try
        {
            await SendAsync(new TunnelHeader
            {
                Kind = FrameKind.Request,
                Id = id,
                Method = method,
                Path = pathAndQuery,
                Headers = headers,
            }, body, ct);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
            deadline.CancelAfter(timeout);

            // The session dying must fail the wait immediately. Without this the
            // caller would sit here for the full timeout after the owner closed
            // their laptop, and aixman would hold the job that long for nothing.
            await using (deadline.Token.Register(() => waiter.TrySetCanceled(deadline.Token)))
            {
                return await waiter.Task;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closed.CancelAsync();

        // Anything still waiting would otherwise hang until its own timeout.
        foreach (var kv in _pending)
        {
            kv.Value.TrySetException(new IOException($"Agent {WorkerId} disconnected"));
        }
        _pending.Clear();

        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "relay closing", cts.Token);
            }
            catch
            {
                // The socket is going away regardless; a failure to say so politely is not interesting.
            }
        }

        _socket.Dispose();
        _writeGate.Dispose();
        _closed.Dispose();
    }
}
