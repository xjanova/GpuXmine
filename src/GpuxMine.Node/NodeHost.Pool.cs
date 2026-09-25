using GpuxMine.Core.Licensing;

namespace GpuxMine.Node;

/// <summary>
/// Asking XMAN Studio what the pool makes of this machine and what its jobs
/// paid (contract C6), and writing the payouts into the ledger.
/// </summary>
/// <remarks>
/// <para>
/// Two gaps closed by one call. The owner saw "sharing" while the pool had
/// set the machine aside, and nobody said why; and every money figure in the
/// program was a dash, because <see cref="Storage.NodeStore.JobSettled"/> had
/// no caller — nothing ever told the node what a job had paid.
/// </para>
/// <para>
/// Every three minutes while sharing, every fifteen while stopped, once at
/// launch, and at once after a pairing, a START or the owner pressing refresh.
/// A refused identity waits half an hour and a website that predates the call
/// an hour; an outage backs off from three minutes to thirty. None of it can
/// stop the node rendering.
/// </para>
/// </remarks>
public sealed partial class NodeHost
{
    internal static readonly TimeSpan PoolPollSharing = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan PoolPollStopped = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan PoolPollRejected = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan PoolPollNotSupported = TimeSpan.FromMinutes(60);
    internal static readonly TimeSpan PoolPollFailureCap = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The least time between two calls, however often refresh is pressed.
    /// XMAN Studio allows thirty a minute per machine; a double click or an
    /// owner hammering the button should cost one call, not thirty.
    /// </summary>
    internal static readonly TimeSpan PoolMinSpacing = TimeSpan.FromSeconds(15);

    /// <summary>Released to cut the wait short. Holds at most one: ten presses are one refresh.</summary>
    private readonly SemaphoreSlim _poolKick = new(0, 1);

    private int _poolFailures;
    private volatile string? _poolLastSaid;

    /// <summary>
    /// Asks XMAN Studio again as soon as the spacing allows — after a pairing,
    /// a START, or the owner pressing refresh. Never blocks, never throws.
    /// </summary>
    public void RefreshPoolStatus()
    {
        try { _poolKick.Release(); }
        catch (SemaphoreFullException) { /* a refresh is already due */ }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private async Task PoolStatusLoopAsync(CancellationToken ct)
    {
        // After the licence check and device registration have had their go,
        // so launch is not three calls to the same website in one second.
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await PollPoolStatusOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad pass is not a reason to stop asking for the rest of
                // the process's life.
                Log.Warn($"[warn] pool status pass failed: {ex.Message}");
                wait = PoolPollSharing;
            }

            try
            {
                await Task.Delay(PoolMinSpacing, ct);
                await _poolKick.WaitAsync(wait > PoolMinSpacing ? wait - PoolMinSpacing : TimeSpan.Zero, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One call: asks, records what the jobs paid, publishes the pool's view,
    /// and says when to ask next.
    /// </summary>
    internal async Task<TimeSpan> PollPoolStatusOnceAsync(CancellationToken ct)
    {
        TimeSpan cadence = State.Running ? PoolPollSharing : PoolPollStopped;

        if (!Options.Validate(out _))
        {
            Publish(State.Pool with { Outcome = PoolOutcome.Unpaired, Last = null, LastOkAt = null, Problem = null, LedgerChanges = 0 });
            return cadence;
        }

        // Whose answer this is. A pairing that lands while the call is out
        // replaces the identity, and the answer about the old one must not be
        // shown — or written into the ledger — as if it were about the new.
        string workerId = Options.WorkerId, token = Options.Token;

        NodeStatusResult result = await _studio.StatusAsync(workerId, token, ct);
        DateTimeOffset now = DateTimeOffset.Now;

        if (!string.Equals(Options.WorkerId, workerId, StringComparison.Ordinal)
            || !string.Equals(Options.Token, token, StringComparison.Ordinal))
        {
            return TimeSpan.Zero;   // ask again, about the machine it is now
        }

        switch (result.Outcome)
        {
            case StudioOutcome.Ok when result.Status is { } status:
                _poolFailures = 0;
                int changed = RecordSettlements(status);
                Publish(new PoolView
                {
                    Outcome = PoolOutcome.Ok,
                    Last = status,
                    LastOkAt = now,
                    CheckedAt = now,
                    LedgerChanges = changed,
                });
                return cadence;

            case StudioOutcome.IdentityRejected:
                _poolFailures = 0;
                // The last answer was about an identity the website has since
                // let go of; nothing in it can be relied on now.
                Publish(new PoolView
                {
                    Outcome = PoolOutcome.IdentityRejected,
                    CheckedAt = now,
                    Problem = result.Message,
                });
                return PoolPollRejected;

            case StudioOutcome.NotSupported:
                _poolFailures = 0;
                Publish(new PoolView { Outcome = PoolOutcome.NotSupported, CheckedAt = now });
                return PoolPollNotSupported;

            default:
                _poolFailures++;
                Publish(State.Pool with
                {
                    Outcome = PoolOutcome.Unavailable,
                    CheckedAt = now,
                    Problem = result.Message ?? "ติดต่อ XMAN Studio ไม่ได้",
                    LedgerChanges = 0,
                });
                // 3, 6, 12, 24, then 30 minutes: a website that is down for an
                // afternoon costs a handful of calls, not eighty. Never sooner
                // than the ordinary cadence.
                TimeSpan backoff = TimeSpan.FromMinutes(Math.Min(
                    PoolPollSharing.TotalMinutes * Math.Pow(2, Math.Min(_poolFailures - 1, 10)),
                    PoolPollFailureCap.TotalMinutes));
                return backoff > cadence ? backoff : cadence;
        }
    }

    /// <summary>
    /// Writes what each of this machine's jobs was settled at into the ledger,
    /// and tells the owner about what changed — once, not on every poll.
    /// </summary>
    /// <returns>How many ledger rows changed.</returns>
    private int RecordSettlements(NodeStatus status)
    {
        if (status.Jobs is not { Count: > 0 } jobs) return 0;

        var moved = new List<SettledJob>();
        foreach (SettledJob job in jobs)
        {
            // A row aixman wrote without a prompt id cannot be matched to
            // anything here, and a guess would pay the wrong job.
            if (string.IsNullOrWhiteSpace(job.PromptId)) continue;

            try
            {
                if (Store.JobSettled(job.PromptId, job.AmountSatang, job.Status, job.DonatedValueSatang, job.JobId))
                    moved.Add(job);
            }
            catch (Exception ex)
            {
                // The ledger is a mirror; the website still holds the record.
                Log.Warn($"[warn] บันทึกค่าตอบแทนของงาน {Short(job.PromptId)} ไม่ได้: {ex.Message}");
            }
        }

        if (moved.Count > 0)
        {
            int? hold = status.Earnings?.HoldHours;
            string parts = string.Join(" · ", moved
                .GroupBy(j => j.Status ?? "?", StringComparer.Ordinal)
                .Select(g => $"{PoolView.DescribePayoutStatus(g.Key == "?" ? null : g.Key, hold)} {g.Count()} งาน {PoolView.Baht(g.Sum(j => j.AmountSatang))}"));
            Log.Info($"[pay] XMAN Studio อัปเดตค่าตอบแทน {moved.Count} งานของเครื่องนี้ — {parts}");
        }

        return moved.Count;
    }

    /// <summary>
    /// Publishes the pool's view, and puts a line in the log when what it says
    /// has changed — the headless agent's only screen is its log.
    /// </summary>
    private void Publish(PoolView view)
    {
        State.SetPool(view);

        string said = $"{view.Outcome}|{view.Headline}|{view.Alert}";
        if (string.Equals(said, _poolLastSaid, StringComparison.Ordinal)) return;
        _poolLastSaid = said;

        switch (view.Outcome)
        {
            case PoolOutcome.Unpaired:
                return;   // Settings already asks for a pairing code

            case PoolOutcome.Unavailable:
                // An outage is one fact, not a line every few minutes; and a
                // failure right after launch is usually the network not being
                // up yet, which the next pass says nothing about.
                Log.Info($"[pool] อ่านสถานะจาก XMAN Studio ไม่ได้ — {view.Problem} · จะลองใหม่เอง");
                return;

            case PoolOutcome.NotSupported:
                Log.Info("[pool] XMAN Studio รุ่นนี้ยังไม่ส่งสถานะ pool ให้โปรแกรม — ดูที่หน้าเว็บแทน");
                return;
        }

        if (view.Alert is { } alert)
        {
            Log.Warn($"[pool] {alert}");
            return;
        }

        string detail = view.Detail is { Length: > 0 } d ? $" — {d}" : "";
        Log.Info($"[pool] {view.Headline}{detail}");
    }

    /// <summary>The pool's view no longer applies: the identity it was about has been replaced.</summary>
    private void ForgetPool()
    {
        _poolFailures = 0;
        _poolLastSaid = null;
        State.SetPool(PoolView.None);
        RefreshPoolStatus();
    }
}
