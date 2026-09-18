using System.Net.WebSockets;
using GpuxMine.Protocol;

namespace GpuxMine.Agent;

/// <summary>
/// The node's one connection to the outside world: dialled out, never listened
/// for. That is the whole reason this project can work on a home PC behind
/// CGNAT without asking its owner to forward a port.
/// </summary>
public sealed class RelayConnection(AgentOptions options, ComfyRuntime runtime, ILoggerish log)
{
    private static readonly string AgentVersion =
        typeof(RelayConnection).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task RunForeverAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(ct);
                attempt = 0; // a session that actually ran resets the backoff
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Warn($"relay connection failed: {ex.Message}");
            }

            if (ct.IsCancellationRequested) return;

            // Jitter matters more here than it looks: when the relay restarts,
            // every node in the fleet is disconnected in the same instant and
            // would otherwise stampede back in lockstep.
            TimeSpan wait = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))))
                            + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3000));
            attempt++;
            log.Info($"reconnecting in {wait.TotalSeconds:0}s");
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {options.Token}");
        socket.Options.SetRequestHeader("X-Worker-Id", options.WorkerId);
        socket.Options.SetRequestHeader("X-Agent-Version", AgentVersion);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        await socket.ConnectAsync(new Uri(options.RelayUrl), ct);
        log.Info($"connected to relay as {options.WorkerId}");

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await SendAsync(socket, new TunnelHeader
        {
            Kind = FrameKind.Hello,
            WorkerId = options.WorkerId,
            AgentVersion = AgentVersion,
            Runtimes = ["comfyui"],
        }, ReadOnlyMemory<byte>.Empty, session.Token);

        Task heartbeat = HeartbeatLoopAsync(socket, session.Token);

        try
        {
            await ReceiveLoopAsync(socket, session.Token);
        }
        finally
        {
            await session.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { /* expected */ }

            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "agent stopping", closing.Token);
                }
                catch { /* going away regardless */ }
            }
            log.Info("relay session ended");
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var assembled = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;

                if (assembled.Length + result.Count > WireCodec.MaxFrameBytes)
                    throw new InvalidDataException("relay sent an oversized frame");

                assembled.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            byte[] frame = assembled.ToArray();
            assembled.SetLength(0);

            TunnelHeader header;
            byte[] body;
            try
            {
                (header, body) = WireCodec.Decode(frame);
            }
            catch (Exception ex)
            {
                log.Warn($"undecodable frame from relay: {ex.Message}");
                continue;
            }

            switch (header.Kind)
            {
                case FrameKind.HelloAck:
                    log.Info("relay accepted the session");
                    break;

                case FrameKind.Request:
                    // Deliberately not awaited: aixman polls /history and
                    // /aixman/progress while a render is in flight, and handling
                    // one request at a time would serialise them behind a call
                    // that can take a minute.
                    _ = HandleRequestAsync(socket, header, body, ct);
                    break;

                case FrameKind.Bye:
                    log.Info($"relay closed the session: {header.Reason}");
                    return;
            }
        }
    }

    private async Task HandleRequestAsync(ClientWebSocket socket, TunnelHeader header, byte[] body, CancellationToken ct)
    {
        LocalReply reply;
        try
        {
            reply = await runtime.HandleAsync(
                header.Method ?? "GET",
                header.Path ?? "/",
                header.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                body,
                ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            log.Warn($"handler for {header.Method} {header.Path} threw: {ex.Message}");
            reply = LocalReply.Json(500, new { error = "agent handler failed", detail = ex.Message });
        }

        try
        {
            await SendAsync(socket, new TunnelHeader
            {
                Kind = FrameKind.Response,
                Id = header.Id,
                Status = reply.Status,
                Headers = reply.Headers,
            }, reply.Body, ct);
        }
        catch (Exception ex)
        {
            // The socket died while we were working. The relay has already
            // failed the waiter; there is nothing left to answer.
            log.Warn($"could not return a reply for {header.Path}: {ex.Message}");
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(Math.Max(3, options.HeartbeatSeconds));
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            try
            {
                await SendAsync(socket, new TunnelHeader
                {
                    Kind = FrameKind.Heartbeat,
                    // Real GPU numbers arrive in M3 with LibreHardwareMonitor.
                    // Reporting zeroes is honest; inventing plausible ones would
                    // put fiction on the pool's dashboards from day one.
                    Telemetry = new AgentTelemetry { Accepting = true, FreeSharePct = 0 },
                }, ReadOnlyMemory<byte>.Empty, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Warn($"heartbeat failed: {ex.Message}");
                return;
            }

            try { await Task.Delay(period, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task SendAsync(ClientWebSocket socket, TunnelHeader header, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        byte[] frame = WireCodec.Encode(header, body.Span);
        await _writeGate.WaitAsync(ct);
        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
