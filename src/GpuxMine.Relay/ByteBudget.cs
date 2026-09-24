namespace GpuxMine.Relay;

/// <summary>
/// A ceiling on bytes held in memory on someone's behalf — one reply, one
/// agent's session, or the whole relay — that callers reserve from before they
/// buffer and give back once the bytes are gone.
/// </summary>
/// <remarks>
/// The relay's memory is shared by every node in the fleet. Before this, a
/// single agent could make it hold a 192 MB frame per session, and a handful
/// of them could get the process OOM-killed — which drops every other node
/// with it. Budgets turn "one peer is sending too much" into that peer's
/// reply being cut off, instead of everyone's session ending.
/// </remarks>
public sealed class ByteBudget(long capacity)
{
    private readonly Lock _gate = new();
    private long _used;
    private TaskCompletionSource _released = NewSignal();

    public long Capacity { get; } = capacity;

    public long Used
    {
        get { lock (_gate) return _used; }
    }

    public bool TryReserve(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_gate)
        {
            if (_used + bytes > Capacity) return false;
            _used += bytes;
            return true;
        }
    }

    public void Release(long bytes)
    {
        if (bytes <= 0) return;
        TaskCompletionSource signal;
        lock (_gate)
        {
            _used = Math.Max(0, _used - bytes);
            signal = _released;
            _released = NewSignal();
        }
        signal.TrySetResult();
    }

    /// <summary>Completes the next time anything is released. Take it before trying to reserve, or a release in between is missed.</summary>
    public Task WhenReleased()
    {
        lock (_gate) return _released.Task;
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> from every budget in
    /// <paramref name="budgets"/> or from none, waiting up to
    /// <paramref name="stall"/> for room.
    /// </summary>
    /// <returns>False when there was still no room when the wait ran out.</returns>
    public static async ValueTask<bool> ReserveAllAsync(long bytes, ByteBudget[] budgets, TimeSpan stall, CancellationToken ct)
    {
        foreach (ByteBudget budget in budgets)
        {
            // Would never fit, however long we waited.
            if (bytes > budget.Capacity) return false;
        }

        long deadline = Environment.TickCount64 + (long)stall.TotalMilliseconds;
        while (true)
        {
            var waits = new Task[budgets.Length];
            for (int i = 0; i < budgets.Length; i++) waits[i] = budgets[i].WhenReleased();

            if (TryReserveAll(bytes, budgets)) return true;

            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return false;

            await Task.WhenAny(Task.WhenAny(waits), Task.Delay(TimeSpan.FromMilliseconds(remaining), ct));
            ct.ThrowIfCancellationRequested();
        }
    }

    public static bool TryReserveAll(long bytes, ByteBudget[] budgets)
    {
        for (int i = 0; i < budgets.Length; i++)
        {
            if (budgets[i].TryReserve(bytes)) continue;
            for (int j = 0; j < i; j++) budgets[j].Release(bytes);
            return false;
        }
        return true;
    }

    public static void ReleaseAll(long bytes, ByteBudget[] budgets)
    {
        foreach (ByteBudget budget in budgets) budget.Release(bytes);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
