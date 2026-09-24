using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using GpuxMine.Protocol;

namespace GpuxMine.Node;

/// <summary>
/// The node's one connection to the outside world: dialled out, never listened
/// for. That is the whole reason this project can work on a home PC behind
/// CGNAT without asking its owner to forward a port.
/// </summary>
/// <remarks>
/// Replies go back in whichever shape the relay said it understands. A relay
/// that lists <see cref="TunnelCaps.ResponseStreaming"/> in its hello gets the
/// reply as a head, 1 MB chunks and an end, which it passes to aixman as they
/// arrive; any other relay — the one already in production when this build
/// ships — gets one <see cref="FrameKind.Response"/> frame exactly as v0.1 sent.
/// </remarks>
public sealed class RelayConnection(
    NodeOptions options,
    ComfyRuntime runtime,
    ILoggerish log,
    Func<AgentTelemetry>? telemetry = null)
{
    /// <summary>Fires on connect and disconnect so a host can show the state without polling.</summary>
    public event Action<bool>? ConnectedChanged;

    /// <summary>
    /// Fires when the relay refuses the connection outright: 401 (unknown
    /// worker or wrong token — the node needs pairing again) or 403 (an
    /// operator switched the worker off). Retrying fast will not change either,
    /// so the loop backs off for minutes after one.
    /// </summary>
    public event Action<int>? Refused;

    private static readonly string AgentVersion =
        typeof(RelayConnection).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    /// <summary>How long to wait before trying again after the relay said 401 or 403.</summary>
    private static readonly TimeSpan RefusedBackoff = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>What one connection learned from the relay, and what is running on it.</summary>
    private sealed class Session(ClientWebSocket socket, CancellationToken token)
    {
        public ClientWebSocket Socket { get; } = socket;

        /// <summary>Cancelled when this connection ends. Every socket write uses it, and nothing shorter.</summary>
        public CancellationToken Token { get; } = token;

        public volatile bool Streaming;
        public int ChunkBytes = WireCodec.MaxChunkBytes;

        /// <summary>Requests being worked on, so a cancel from the relay can stop the right one.</summary>
        public ConcurrentDictionary<string, CancellationTokenSource> Running { get; } = new(StringComparer.Ordinal);
    }

    public async Task RunForeverAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan? wait = null;
            try
            {
                await ConnectOnceAsync(ct);
                attempt = 0; // a session that actually ran resets the backoff
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (RelayRefusedException ex)
            {
                log.Warn(ex.Status == 403
                    ? "relay refused this worker: it has been disabled (HTTP 403)"
                    : "relay refused this worker: unknown worker or wrong token — pair the machine again (HTTP 401)");
                Refused?.Invoke(ex.Status);
                wait = RefusedBackoff;
            }
            catch (Exception ex)
            {
                log.Warn($"relay connection failed: {ex.Message}");
            }

            if (ct.IsCancellationRequested) return;

            // Jitter matters more here than it looks: when the relay restarts,
            // every node in the fleet is disconnected in the same instant and
            // would otherwise stampede back in lockstep.
            wait ??= TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));
            wait += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3000));
            attempt++;
            log.Info($"reconnecting in {wait.Value.TotalSeconds:0}s");
            try { await Task.Delay(wait.Value, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The relay answered the upgrade with a status that retrying will not fix.</summary>
    private sealed class RelayRefusedException(int status)
        : Exception($"relay refused the connection (HTTP {status})")
    {
        public int Status { get; } = status;
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {options.Token}");
        socket.Options.SetRequestHeader("X-Worker-Id", options.WorkerId);
        socket.Options.SetRequestHeader("X-Agent-Version", AgentVersion);
        socket.Options.SetRequestHeader("User-Agent", Core.Net.NodeHttp.UserAgent);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        // A relay that vanished without closing (its host lost power, a NAT
        // table dropped us) otherwise looks connected until TCP gives up —
        // minutes on Windows, about fifteen on Linux. Generous, because the
        // relay may take a while to answer a ping while it is moving a large
        // upload down this same socket.
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(90);
        // So a refusal can be told apart from a network failure: 401/403 mean
        // "stop knocking", anything else means "try again soon".
        socket.Options.CollectHttpResponseDetails = true;

        try
        {
            await socket.ConnectAsync(new Uri(options.RelayUrl), ct);
        }
        catch (WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new RelayRefusedException((int)socket.HttpStatusCode);
        }

        log.Info($"[net] connected to relay as {options.WorkerId}");
        ConnectedChanged?.Invoke(true);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var session = new Session(socket, sessionCts.Token);

        try
        {
            await SendAsync(session, new TunnelHeader
            {
                Kind = FrameKind.Hello,
                WorkerId = options.WorkerId,
                AgentVersion = AgentVersion,
                Runtimes = ["comfyui"],
            }, ReadOnlyMemory<byte>.Empty, session.Token);

            Task heartbeat = HeartbeatLoopAsync(session);

            try
            {
                await ReceiveLoopAsync(session);
            }
            finally
            {
                await sessionCts.CancelAsync();
                try { await heartbeat; } catch (OperationCanceledException) { /* expected */ }
            }
        }
        finally
        {
            // Requests still being worked on end with the session: they are
            // linked to its token, which the inner finally has cancelled.
            // CloseReceived too: answering the relay's close lets it finish the
            // handshake instead of waiting and then cutting the connection.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "agent stopping", closing.Token);
                }
                catch { /* going away regardless */ }
            }
            log.Info("[net] relay session ended");
            ConnectedChanged?.Invoke(false);
        }
    }

    private async Task ReceiveLoopAsync(Session session)
    {
        ClientWebSocket socket = session.Socket;
        CancellationToken ct = session.Token;
        var buffer = new byte[64 * 1024];
        var assembled = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.CloseStatus is WebSocketCloseStatus.PolicyViolation)
                        log.Warn($"relay closed the session: {socket.CloseStatusDescription}");
                    return;
                }

                if (assembled.Length + result.Count > WireCodec.MaxFrameBytes)
                    throw new InvalidDataException("relay sent an oversized frame");

                assembled.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            byte[] frame = assembled.ToArray();
            // One large upload must not pin that much memory for the rest of
            // the session.
            if (assembled.Capacity > 4 * 1024 * 1024) assembled = new MemoryStream();
            else assembled.SetLength(0);

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
                    session.Streaming = header.Caps?.Contains(TunnelCaps.ResponseStreaming) == true;
                    session.ChunkBytes = Math.Clamp(header.MaxChunkBytes ?? WireCodec.MaxChunkBytes, 16 * 1024, WireCodec.MaxChunkBytes);
                    log.Info(session.Streaming
                        ? "relay accepted the session (streamed replies)"
                        : "relay accepted the session");
                    break;

                case FrameKind.Request:
                    // Deliberately not awaited: aixman polls /history and
                    // /aixman/progress while a render is in flight, and handling
                    // one request at a time would serialise them behind a call
                    // that can take a minute.
                    _ = HandleRequestAsync(session, header, body);
                    break;

                case FrameKind.Cancel:
                    if (header.Id is not null && session.Running.TryRemove(header.Id, out CancellationTokenSource? cancel))
                    {
                        try { cancel.Cancel(); } catch (ObjectDisposedException) { /* finished meanwhile */ }
                    }
                    break;

                case FrameKind.Bye:
                    log.Info($"relay closed the session: {header.Reason}");
                    return;
            }
        }
    }

    private async Task HandleRequestAsync(Session session, TunnelHeader header, byte[] body)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        if (header.Id is not null) session.Running[header.Id] = request;

        try
        {
            LocalReply reply;
            try
            {
                reply = await runtime.HandleAsync(
                    header.Method ?? "GET",
                    header.Path ?? "/",
                    header.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    body,
                    request.Token);
            }
            catch (OperationCanceledException)
            {
                return; // the relay gave up on it, or the session ended
            }
            catch (Exception ex)
            {
                log.Warn($"handler for {header.Method} {header.Path} threw: {ex.Message}");
                reply = LocalReply.Json(500, new { error = "agent handler failed", detail = ex.Message });
            }

            try
            {
                if (session.Streaming)
                    await SendStreamedAsync(session, header.Id, reply, request.Token);
                else
                    await SendWholeAsync(session, header, reply);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                // Cancelled by the relay part-way through. It has already
                // stopped listening for this id; nothing more to send.
            }
            catch (Exception ex)
            {
                // The socket died while we were working. The relay has already
                // failed the waiter; there is nothing left to answer.
                log.Warn($"could not return a reply for {header.Path}: {ex.Message}");
            }
        }
        finally
        {
            if (header.Id is not null)
                session.Running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(header.Id, request));
        }
    }

    /// <summary>
    /// v0.1's single <see cref="FrameKind.Response"/>. A reply too large for
    /// the old relay's frame ceiling is answered 413 instead: sent whole, it
    /// would end the session and every other request riding on it.
    /// </summary>
    private async Task SendWholeAsync(Session session, TunnelHeader request, LocalReply reply)
    {
        if (reply.Body.Length > WireCodec.MaxFrameBytes - WireCodec.MaxHeaderBytes - WireCodec.PrefixBytes)
        {
            log.Warn($"reply to {request.Path} is {reply.Body.Length / (1024 * 1024)} MB, over what this relay accepts in one frame — answering 413");
            reply = LocalReply.Json(413, new { error = "reply-too-large", bytes = reply.Body.Length });
        }

        await SendAsync(session, new TunnelHeader
        {
            Kind = FrameKind.Response,
            Id = request.Id,
            Status = reply.Status,
            Headers = reply.Headers,
        }, reply.Body, session.Token);
    }

    /// <summary>
    /// Head, chunks, end — each frame taking the write gate on its own, so a
    /// heartbeat or another request's answer can go out between two chunks of
    /// a large file instead of waiting behind all of it.
    /// </summary>
    private async Task SendStreamedAsync(Session session, string? id, LocalReply reply, CancellationToken requestToken)
    {
        await SendAsync(session, new TunnelHeader
        {
            Kind = FrameKind.ResponseHead,
            Id = id,
            Status = reply.Status,
            Headers = reply.Headers,
        }, ReadOnlyMemory<byte>.Empty, requestToken);

        try
        {
            for (int offset = 0; offset < reply.Body.Length; offset += session.ChunkBytes)
            {
                requestToken.ThrowIfCancellationRequested();
                int length = Math.Min(session.ChunkBytes, reply.Body.Length - offset);
                await SendAsync(session, new TunnelHeader { Kind = FrameKind.ResponseChunk, Id = id },
                    reply.Body.AsMemory(offset, length), requestToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && session.Socket.State == WebSocketState.Open)
        {
            // Say so, so the relay cuts aixman's response off instead of
            // ending it as if the file were complete.
            await SendAsync(session, new TunnelHeader { Kind = FrameKind.ResponseEnd, Id = id, Aborted = true },
                ReadOnlyMemory<byte>.Empty, session.Token);
            throw;
        }

        await SendAsync(session, new TunnelHeader { Kind = FrameKind.ResponseEnd, Id = id },
            ReadOnlyMemory<byte>.Empty, requestToken);
    }

    private async Task HeartbeatLoopAsync(Session session)
    {
        CancellationToken ct = session.Token;
        var period = TimeSpan.FromSeconds(Math.Max(3, options.HeartbeatSeconds));
        while (!ct.IsCancellationRequested && session.Socket.State == WebSocketState.Open)
        {
            try
            {
                // Whatever the host can measure. A host with no sensing sends
                // zeros — honest, where plausible-looking numbers would put
                // fiction on the pool's dashboards.
                AgentTelemetry payload = telemetry?.Invoke() ?? new AgentTelemetry { Accepting = true };

                await SendAsync(session, new TunnelHeader
                {
                    Kind = FrameKind.Heartbeat,
                    Telemetry = payload,
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

    /// <summary>
    /// <paramref name="gate"/> bounds only the wait for the write gate. The
    /// write itself runs on the session's token: cancelling a WebSocket send
    /// part-way aborts the socket, and one request the relay gave up on would
    /// take every other one in flight down with it.
    /// </summary>
    private async Task SendAsync(Session session, TunnelHeader header, ReadOnlyMemory<byte> body, CancellationToken gate)
    {
        byte[] frame = WireCodec.Encode(header, body.Span);
        await _writeGate.WaitAsync(gate);
        try
        {
            await session.Socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, session.Token);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
