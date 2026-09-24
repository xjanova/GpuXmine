using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using GpuxMine.Protocol;

namespace GpuxMine.Relay;

/// <summary>
/// One connected agent. Owns the socket for its whole life: every write goes
/// through <see cref="_writeGate"/> because a WebSocket permits exactly one
/// send at a time, and aixman happily issues concurrent requests (a health
/// probe while a render is being polled).
/// </summary>
/// <remarks>
/// <para>
/// Frames are read piece by piece, never assembled whole. The header comes
/// first and says what the body is for; the body then goes straight to the
/// aixman request waiting for it, or — when nobody is waiting for that id any
/// more — nowhere. The relay used to assemble every frame in a buffer that
/// grew to the largest frame the node ever sent and stayed that size; one
/// peer sending a few 192 MB frames could take the process, and every other
/// node's session with it.
/// </para>
/// <para>
/// What the relay holds for this node is bounded by a per-session budget and
/// the relay-wide one. When aixman reads slower than the node sends, the
/// reader waits for room — which in turn stops reading the socket and lets
/// TCP slow the node down — and gives up on that one reply if the wait runs
/// long.
/// </para>
/// </remarks>
public sealed class AgentSession : IAsyncDisposable
{
    /// <summary>Largest read from the socket at a time, and the size of the pooled blocks the body travels in.</summary>
    private const int BlockBytes = 64 * 1024;

    /// <summary>Pieces this small are copied out so a 64 KB block is not charged for a few bytes.</summary>
    private const int CopyBelowBytes = 8 * 1024;

    private readonly WebSocket _socket;
    private readonly ILogger _log;
    private readonly RelayOptions _options;
    private readonly ByteBudget _sessionBudget;
    private readonly ByteBudget _globalBudget;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TunnelExchange> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _runEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastSeenTicks = DateTimeOffset.UtcNow.UtcTicks;
    private int _disposed;

    public string WorkerId { get; }
    public string? AgentVersion { get; }
    public string? RemoteIp { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>When anything last arrived from the node — a heartbeat, a reply, a single byte of one.</summary>
    public DateTimeOffset LastSeenAt => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);

    public AgentTelemetry? Telemetry { get; private set; }
    public CancellationToken Closed => _closed.Token;

    /// <summary>Requests sent to the node that have not finished yet.</summary>
    public int InFlight => _pending.Count;

    public AgentSession(
        string workerId,
        string? agentVersion,
        string? remoteIp,
        WebSocket socket,
        RelayOptions options,
        ByteBudget globalBudget,
        ILogger log)
    {
        WorkerId = workerId;
        AgentVersion = agentVersion;
        RemoteIp = remoteIp;
        _socket = socket;
        _options = options;
        _sessionBudget = new ByteBudget(options.SessionBufferBytes);
        _globalBudget = globalBudget;
        _log = log;
    }

    private void Seen() => Interlocked.Exchange(ref _lastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>
    /// Pumps frames until the socket dies. Returns only when the session is over;
    /// the caller keeps the HTTP request alive for exactly that long.
    /// </summary>
    public async Task RunAsync(CancellationToken hostStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStopping, _closed.Token);
        var head = new byte[WireCodec.PrefixBytes + WireCodec.MaxHeaderBytes];

        try
        {
            while (_socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                if (!await ReadFrameAsync(head, linked.Token)) return;
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
        catch (ObjectDisposedException)
        {
            // Disposed from outside (deleted, disabled, swept) while reading.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Agent {WorkerId} session failed", WorkerId);
        }
        finally
        {
            // Closed before the pending requests are failed, so a request that
            // arrives in between is refused at once instead of registering and
            // waiting out its whole timeout on a session that is already gone.
            _closed.Cancel();
            FailPending($"Agent {WorkerId} disconnected");
            _runEnded.TrySetResult();
        }
    }

    /// <summary>Where the body of the frame being read goes.</summary>
    private sealed class FrameRoute
    {
        public static readonly FrameRoute Discard = new(null, isChunk: false, endsReply: false);

        public FrameRoute(TunnelExchange? exchange, bool isChunk, bool endsReply)
        {
            Exchange = exchange;
            IsChunk = isChunk;
            EndsReply = endsReply;
        }

        public TunnelExchange? Exchange { get; }
        public bool IsChunk { get; }
        public bool EndsReply { get; }
        public long BodyBytes { get; set; }
    }

    /// <summary>Reads one WebSocket message — one frame. False when the peer closed.</summary>
    private async Task<bool> ReadFrameAsync(byte[] head, CancellationToken ct)
    {
        int headFill = 0;
        int headerLength = -1;
        FrameRoute? route = null;

        while (true)
        {
            byte[] block = ArrayPool<byte>.Shared.Rent(BlockBytes);
            bool handedOff = false;
            try
            {
                ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(block.AsMemory(0, BlockBytes), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _log.LogInformation("Agent {WorkerId} closed the socket: {Status} {Description}",
                        WorkerId, _socket.CloseStatus, _socket.CloseStatusDescription);
                    return false;
                }

                Seen();
                int offset = 0;
                int count = result.Count;

                // Prefix and header first, into a fixed buffer. Whatever
                // follows them in the same read is already body.
                while (route is null && count > 0)
                {
                    int target = headerLength < 0 ? WireCodec.PrefixBytes : WireCodec.PrefixBytes + headerLength;
                    int take = Math.Min(target - headFill, count);
                    Buffer.BlockCopy(block, offset, head, headFill, take);
                    headFill += take;
                    offset += take;
                    count -= take;
                    if (headFill < target) break;

                    if (headerLength < 0)
                    {
                        if (!WireCodec.TryReadPrefix(head.AsSpan(0, WireCodec.PrefixBytes), out headerLength, out string? problem))
                        {
                            _log.LogWarning("Agent {WorkerId} sent an unreadable frame: {Problem}", WorkerId, problem);
                            route = FrameRoute.Discard;
                        }
                        continue;
                    }

                    route = Open(head, headerLength);
                }

                if (route is null && result.EndOfMessage)
                {
                    _log.LogWarning("Agent {WorkerId} sent a frame that ended inside its own header", WorkerId);
                    route = FrameRoute.Discard;
                }

                if (route is not null && count > 0)
                    handedOff = await AcceptBodyAsync(route, block, offset, count, ct);

                if (result.EndOfMessage)
                {
                    if (route is { EndsReply: true, Exchange: { } finished }) finished.Complete();
                    return true;
                }
            }
            finally
            {
                if (!handedOff) ArrayPool<byte>.Shared.Return(block);
            }
        }
    }

    /// <summary>Acts on a frame's header and decides where its body goes.</summary>
    private FrameRoute Open(byte[] head, int headerLength)
    {
        TunnelHeader header;
        try
        {
            header = WireCodec.DecodeHeader(head.AsSpan(WireCodec.PrefixBytes, headerLength));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Agent {WorkerId} sent an undecodable frame: {Message}", WorkerId, ex.Message);
            return FrameRoute.Discard;
        }

        switch (header.Kind)
        {
            case FrameKind.Response:
            {
                // v0.1's one-frame reply. Streamed through as it arrives all the
                // same: the header is the head, the body is the body, and the
                // end of the message is the end of the reply.
                TunnelExchange? exchange = Find(header.Id);
                if (exchange is null) return FrameRoute.Discard;
                if (!exchange.TryStart(header.Status ?? 502, header.Headers ?? NewHeaders()))
                {
                    exchange.Abort(new TunnelProtocolException("a second reply for the same request"));
                    return FrameRoute.Discard;
                }
                return new FrameRoute(exchange, isChunk: false, endsReply: true);
            }

            case FrameKind.ResponseHead:
            {
                TunnelExchange? exchange = Find(header.Id);
                if (exchange is null) return FrameRoute.Discard;
                if (!exchange.TryStart(header.Status ?? 502, header.Headers ?? NewHeaders()))
                {
                    exchange.Abort(new TunnelProtocolException("a second reply head for the same request"));
                    return FrameRoute.Discard;
                }
                return new FrameRoute(exchange, isChunk: true, endsReply: false);
            }

            case FrameKind.ResponseChunk:
            {
                TunnelExchange? exchange = Find(header.Id);
                if (exchange is null) return FrameRoute.Discard;
                if (!exchange.IsStreaming)
                {
                    exchange.Abort(new TunnelProtocolException("reply body before its head"));
                    return FrameRoute.Discard;
                }
                exchange.Touch();
                return new FrameRoute(exchange, isChunk: true, endsReply: false);
            }

            case FrameKind.ResponseEnd:
            {
                TunnelExchange? exchange = Find(header.Id);
                if (exchange is null) return FrameRoute.Discard;
                if (header.Aborted == true)
                    exchange.Abort(new TunnelProtocolException("the node gave up on its reply"));
                else if (!exchange.IsStreaming)
                    exchange.Abort(new TunnelProtocolException("reply end before its head"));
                else
                    exchange.Complete();
                return FrameRoute.Discard;
            }

            case FrameKind.Heartbeat:
                Telemetry = header.Telemetry;
                return FrameRoute.Discard;

            case FrameKind.Bye:
                _log.LogInformation("Agent {WorkerId} said goodbye: {Reason}", WorkerId, header.Reason);
                _closed.Cancel();
                return FrameRoute.Discard;

            default:
                // hello, and anything a newer agent sends that this build has
                // no use for.
                return FrameRoute.Discard;
        }
    }

    private TunnelExchange? Find(string? id)
        => id is not null && _pending.TryGetValue(id, out TunnelExchange? exchange) && !exchange.IsFinished ? exchange : null;

    private static Dictionary<string, string> NewHeaders() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hands body bytes to the exchange they belong to, waiting for room when
    /// its buffers are full. True when the block now belongs to the exchange.
    /// </summary>
    private async ValueTask<bool> AcceptBodyAsync(FrameRoute route, byte[] block, int offset, int count, CancellationToken ct)
    {
        TunnelExchange? exchange = route.Exchange;
        if (exchange is null || exchange.IsFinished) return false;

        route.BodyBytes += count;
        if (route.IsChunk && route.BodyBytes > WireCodec.MaxChunkBytes)
        {
            exchange.Abort(new TunnelProtocolException($"a reply chunk over {WireCodec.MaxChunkBytes} bytes"));
            return false;
        }
        if (!exchange.AdmitBodyBytes(count)) return false;

        bool copy = count < CopyBelowBytes;
        int charge = copy ? count : block.Length;

        if (!await ByteBudget.ReserveAllAsync(charge, exchange.Budgets, TimeSpan.FromSeconds(_options.StallSeconds), ct))
        {
            _log.LogWarning("Reply {Id} from {WorkerId} abandoned: nobody read it for {Seconds}s", exchange.Id, WorkerId, _options.StallSeconds);
            exchange.Abort(new TunnelProtocolException("the reply's reader stopped reading"));
            return false;
        }

        ReplyPiece piece = copy
            ? new ReplyPiece(block.AsSpan(offset, count).ToArray(), 0, count, charge, Pooled: false)
            : new ReplyPiece(block, offset, count, charge, Pooled: true);

        if (exchange.TryEnqueue(piece)) return piece.Pooled;

        // Finished while we waited for room.
        ByteBudget.ReleaseAll(charge, exchange.Budgets);
        return false;
    }

    /// <summary>
    /// Sends one frame. <paramref name="ct"/> bounds only the wait for the
    /// write gate; once the frame is on its way it is finished or the socket
    /// dies — cancelling a WebSocket send half-way aborts the socket, and one
    /// impatient aixman request would take every other request in flight to
    /// this node with it.
    /// </summary>
    public async Task SendAsync(TunnelHeader header, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        byte[] head = WireCodec.EncodeHead(header);
        await _writeGate.WaitAsync(ct);
        try
        {
            CancellationToken session = _closed.Token;
            // The body goes as a second fragment of the same message, not
            // copied in behind the header: an upload is already in memory once.
            await _socket.SendAsync(head, WebSocketMessageType.Binary, endOfMessage: body.IsEmpty, session);
            if (!body.IsEmpty)
                await _socket.SendAsync(body, WebSocketMessageType.Binary, endOfMessage: true, session);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Sends an HTTP request down the tunnel and returns the exchange its
    /// answer will arrive on. The caller disposes it, which tells the node to
    /// stop if the answer was not taken in full.
    /// </summary>
    public async Task<TunnelExchange> OpenExchangeAsync(
        string method,
        string pathAndQuery,
        Dictionary<string, string> headers,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString("n");
        var exchange = new TunnelExchange(
            id,
            TimeSpan.FromSeconds(_options.TunnelTimeoutSeconds),
            _options.MaxReplyBytes,
            new ByteBudget(Math.Max(BlockBytes, _options.SessionBufferBytes / 2)),
            _sessionBudget,
            _globalBudget);
        _pending[id] = exchange;

        if (_closed.IsCancellationRequested)
        {
            Forget(exchange);
            throw new TunnelOfflineException($"Agent {WorkerId} disconnected");
        }

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
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException
                                   || (ex is OperationCanceledException && _closed.IsCancellationRequested))
        {
            Forget(exchange);
            throw new TunnelOfflineException($"Agent {WorkerId} disconnected: {ex.Message}");
        }
        catch
        {
            Forget(exchange);
            throw;
        }

        return exchange;
    }

    /// <summary>
    /// The handler is done with an exchange. Anything the node sends for it
    /// from now on is dropped as it arrives, and when the reply was not
    /// delivered whole the node is told to stop working on it.
    /// </summary>
    public void Forget(TunnelExchange exchange)
    {
        _pending.TryRemove(new KeyValuePair<string, TunnelExchange>(exchange.Id, exchange));
        bool tellNode = !exchange.CompletedCleanly && !_closed.IsCancellationRequested;
        exchange.Dispose();
        if (tellNode) _ = SendCancelAsync(exchange.Id);
    }

    private async Task SendCancelAsync(string id)
    {
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendAsync(new TunnelHeader { Kind = FrameKind.Cancel, Id = id }, ReadOnlyMemory<byte>.Empty, wait.Token);
        }
        catch
        {
            // Best effort. An agent that never hears it wastes some work; the
            // relay has already stopped listening for the answer.
        }
    }

    private void FailPending(string reason)
    {
        foreach (TunnelExchange exchange in _pending.Values)
            exchange.Abort(new TunnelOfflineException(reason));
    }

    /// <summary>Ends the session, telling the agent why when it can still hear it. Safe to call more than once.</summary>
    public async ValueTask CloseAsync(WebSocketCloseStatus status, string reason)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // The close frame goes out before the read is cancelled: cancelling a
        // pending receive aborts the socket, and the agent would only ever see
        // a dropped connection instead of the reason.
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _writeGate.WaitAsync(cts.Token);
                try
                {
                    await _socket.CloseOutputAsync(status, reason, cts.Token);
                }
                finally
                {
                    _writeGate.Release();
                }

                // A moment for the agent to answer the close, which ends the
                // read loop on its own; cutting the read first would abort the
                // socket with the close frame possibly still unread.
                await Task.WhenAny(_runEnded.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            }
            catch
            {
                // The socket is going away regardless; a failure to say so politely is not interesting.
            }
        }

        await _closed.CancelAsync();

        // Anything still waiting would otherwise hang until its own timeout.
        FailPending($"Agent {WorkerId} disconnected");

        _socket.Abort();
        _socket.Dispose();
    }

    public ValueTask DisposeAsync() => CloseAsync(WebSocketCloseStatus.NormalClosure, "relay closing");
}
