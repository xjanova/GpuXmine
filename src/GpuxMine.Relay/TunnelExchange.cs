using System.Buffers;
using System.Threading.Channels;

namespace GpuxMine.Relay;

/// <summary>Status and headers of a node's reply, known before any of its body.</summary>
public sealed record ReplyHead(int Status, Dictionary<string, string> Headers);

/// <summary>The node disconnected, or was disconnected, before it finished answering.</summary>
public sealed class TunnelOfflineException(string message) : IOException(message);

/// <summary>The node answered with something that is not a reply — a chunk past the limit, frames out of order.</summary>
public sealed class TunnelProtocolException(string message) : Exception(message);

/// <summary>
/// One piece of reply body on its way from the node to aixman. <see cref="Charge"/>
/// is what it costs against the budgets, which for a pooled block is the whole
/// block however little of it is used.
/// </summary>
public readonly record struct ReplyPiece(byte[] Buffer, int Offset, int Count, int Charge, bool Pooled)
{
    public ReadOnlyMemory<byte> Memory => Buffer.AsMemory(Offset, Count);
}

/// <summary>
/// One aixman request waiting on, then streaming, the node's answer.
/// </summary>
/// <remarks>
/// <para>
/// The session's receive loop writes into it as frames arrive; the
/// <c>/w/</c> handler reads from it and writes to aixman. Between the two sits
/// a queue whose size is bounded by three budgets — this reply's, the
/// node's, the relay's — so a node sending faster than aixman reads fills a
/// small buffer and is then made to wait, rather than filling the relay.
/// </para>
/// <para>
/// The same object serves both reply shapes: a v0.1 agent's single
/// <c>res</c> frame is streamed through as it arrives exactly as a
/// <c>res-head</c>/<c>res-chunk</c>/<c>res-end</c> sequence is.
/// </para>
/// </remarks>
public sealed class TunnelExchange : IDisposable
{
    private const int Waiting = 0, Streaming = 1, Completed = 2, Aborted = 3;

    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<ReplyHead> _head = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ReplyPiece> _body = Channel.CreateUnbounded<ReplyPiece>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly CancellationTokenSource _idle;
    private readonly CancellationTokenRegistration _idleRegistration;
    private readonly TimeSpan _idleTimeout;
    private readonly long _maxReplyBytes;
    private int _state = Waiting;
    private bool _disposed;
    private long _bodyBytes;

    public string Id { get; }

    /// <summary>This reply's own budget, then the node's, then the relay's — reserved from in that order.</summary>
    public ByteBudget[] Budgets { get; }

    public Task<ReplyHead> Head => _head.Task;

    public bool IsFinished
    {
        get { lock (_gate) return _state >= Completed || _disposed; }
    }

    /// <summary>True when the reply was delivered whole, which is when nothing has to be cancelled on the node.</summary>
    public bool CompletedCleanly
    {
        get { lock (_gate) return _state == Completed; }
    }

    public TunnelExchange(string id, TimeSpan idleTimeout, long maxReplyBytes, ByteBudget own, ByteBudget session, ByteBudget global)
    {
        Id = id;
        _idleTimeout = idleTimeout;
        _maxReplyBytes = maxReplyBytes;
        Budgets = [own, session, global];

        _idle = new CancellationTokenSource(idleTimeout);
        _idleRegistration = _idle.Token.Register(() =>
            Abort(new TimeoutException($"node sent nothing for {idleTimeout.TotalSeconds:0}s")));
    }

    /// <summary>Something happened on this exchange; the silence timer starts over.</summary>
    public void Touch()
    {
        try
        {
            _idle.CancelAfter(_idleTimeout);
        }
        catch (ObjectDisposedException)
        {
            // Finished while the frame was on its way in.
        }
    }

    /// <summary>The reply's status line arrived. False when one already had, which is a protocol error.</summary>
    public bool TryStart(int status, Dictionary<string, string> headers)
    {
        lock (_gate)
        {
            if (_state != Waiting || _disposed) return false;
            _state = Streaming;
        }
        Touch();
        _head.TrySetResult(new ReplyHead(status, headers));
        return true;
    }

    public bool IsStreaming
    {
        get { lock (_gate) return _state == Streaming && !_disposed; }
    }

    /// <summary>
    /// Counts <paramref name="count"/> more body bytes against the reply
    /// ceiling. False, with the exchange aborted, once it is passed.
    /// </summary>
    public bool AdmitBodyBytes(int count)
    {
        long total = Interlocked.Add(ref _bodyBytes, count);
        if (total <= _maxReplyBytes) return true;

        Abort(new TunnelProtocolException($"reply passed the relay's {_maxReplyBytes}-byte ceiling"));
        return false;
    }

    /// <summary>
    /// Queues a piece whose budget has already been reserved. False when the
    /// exchange has already finished, in which case the caller still owns the
    /// piece and must release it.
    /// </summary>
    public bool TryEnqueue(ReplyPiece piece)
    {
        lock (_gate)
        {
            if (_state != Streaming || _disposed) return false;
            if (!_body.Writer.TryWrite(piece)) return false;
        }
        Touch();
        return true;
    }

    /// <summary>The node says the reply is complete.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_state != Streaming) return;
            _state = Completed;
            _body.Writer.TryComplete();
        }
    }

    /// <summary>
    /// The reply will not be finished. Before its head this fails the wait,
    /// after it the body — and the handler, having already sent a status to
    /// aixman, cuts the connection so the truncation cannot pass for an end.
    /// </summary>
    public void Abort(Exception reason)
    {
        bool beforeHead;
        lock (_gate)
        {
            if (_state >= Completed) return;
            beforeHead = _state == Waiting;
            _state = Aborted;
            _body.Writer.TryComplete(reason);
        }
        if (beforeHead) _head.TrySetException(reason);
    }

    /// <summary>
    /// Reads the next piece of body. Null at a clean end; throws the reason
    /// when the reply was aborted.
    /// </summary>
    public async ValueTask<ReplyPiece?> ReadAsync(CancellationToken ct)
    {
        while (await _body.Reader.WaitToReadAsync(ct))
        {
            if (_body.Reader.TryRead(out ReplyPiece piece)) return piece;
        }
        return null;
    }

    /// <summary>A piece has been written out; its bytes no longer count against anyone.</summary>
    public void Release(ReplyPiece piece)
    {
        ByteBudget.ReleaseAll(piece.Charge, Budgets);
        if (piece.Pooled) ArrayPool<byte>.Shared.Return(piece.Buffer);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_state < Completed)
            {
                _state = Aborted;
                _body.Writer.TryComplete(new OperationCanceledException("exchange closed"));
            }
        }
        _head.TrySetCanceled();

        // Anything still queued was never written out; give its room back.
        while (_body.Reader.TryRead(out ReplyPiece piece)) Release(piece);

        _idleRegistration.Dispose();
        _idle.Dispose();
    }
}
