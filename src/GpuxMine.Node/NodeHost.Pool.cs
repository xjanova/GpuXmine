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
/// <para>
/// Jobs are read by cursor: the website is asked for what changed after the
/// newest <c>updated_at</c> this worker has already seen, which is kept in the
/// ledger. The latest fifty, which is all the call used to return, is less
/// than a fast machine finishes inside one hold window — its older jobs went
/// on being cleared, paid and voided on the website and stayed "pending" here.
/// </para>
/// </remarks>
public sealed partial class NodeHost
{
    internal static readonly TimeSpan PoolPollSharing = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan PoolPollStopped = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan PoolPollRejected = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan PoolPollNotSupported = TimeSpan.FromMinutes(60);
    internal static readonly TimeSpan PoolPollFailureCap = TimeSpan.FromMinutes(30);

    /// <summary>The ledger setting that holds the job cursor, and whose worker it belongs to.</summary>
    internal const string PoolCursorSetting = "pool-status-cursor";

    /// <summary>
    /// Calls one pass may make while the website says more changed jobs
    /// follow. XMAN Studio allows ten a minute per machine, and passes are at
    /// least <see cref="PoolMinSpacing"/> apart however often refresh is
    /// pressed — two calls a pass stays under it even then. Two pages is four
    /// hundred jobs, far more than a machine changes between two polls; the
    /// rest of a backlog waits for <see cref="PoolCatchUp"/>.
    /// </summary>
    internal const int PoolPagesPerPass = 2;

    /// <summary>How soon the next pass comes when a backlog is still being read.</summary>
    internal static readonly TimeSpan PoolCatchUp = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How far back a worker with no cursor yet starts reading, beyond the
    /// hold: a job finished just before the hold window began is still waiting
    /// to be cleared and paid, and a review can take an administrator a day or two.
    /// </summary>
    internal static readonly TimeSpan PoolCursorMargin = TimeSpan.FromHours(48);

    /// <summary>The hold XMAN Studio uses unless configured otherwise — the start's guess until the website has said.</summary>
    internal const int PoolAssumedHoldHours = 24;

    /// <summary>
    /// The least time between two passes, however often refresh is pressed.
    /// XMAN Studio allows ten calls a minute per machine; a double click or an
    /// owner hammering the button should cost one pass, not ten.
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

        // Where this worker's jobs were last read up to. None — first run, a
        // new pairing, a website that never took the cursor — starts one hold
        // plus two days back, so the jobs still moving towards the wallet are
        // all read once; the hold is the website's own once it has said.
        DateTimeOffset? kept = LoadPoolCursor(workerId);
        DateTimeOffset cursor = kept ?? PoolCursorStart(State.Pool.Earnings?.HoldHours ?? PoolAssumedHoldHours, DateTimeOffset.UtcNow);

        NodeStatusResult result = await _studio.StatusAsync(workerId, token, cursor, ct);
        DateTimeOffset now = DateTimeOffset.Now;

        if (!SameIdentity(workerId, token))
        {
            return TimeSpan.Zero;   // ask again, about the machine it is now
        }

        switch (result.Outcome)
        {
            case StudioOutcome.Ok when result.Status is { } status:
                _poolFailures = 0;
                if (await ReadJobPagesAsync(workerId, token, status, cursor, fresh: kept is null, ct) is not { } pass)
                    return TimeSpan.Zero;   // re-paired between two pages

                var view = new PoolView
                {
                    Outcome = PoolOutcome.Ok,
                    Last = pass.Latest,
                    LastOkAt = now,
                    CheckedAt = now,
                    LedgerChanges = pass.Moved.Count,
                };
                Publish(view);
                SayWhatWasPaid(pass.Moved, pass.Latest.Earnings?.HoldHours);
                NoteSuspension(view.Suspended);
                return pass.More && PoolCatchUp < cadence ? PoolCatchUp : cadence;

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

    /// <summary>Whether the last answer from XMAN Studio said an administrator had suspended this machine.</summary>
    private bool _poolSawSuspended;

    /// <summary>
    /// A connection waiting out the relay's 403 while XMAN Studio says the
    /// machine is not suspended tries again now, instead of when the wait runs
    /// out — five minutes after the first refusal, doubling to half an hour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A machine resumed by an administrator used to stay off the relay, with
    /// no work, for up to that long while every page said it was fine.
    /// </para>
    /// <para>
    /// XMAN Studio is where a suspension lives, and the relay's gate follows it:
    /// gpuxmine:sync-nodes enables, every minute, a worker XMAN Studio has not
    /// suspended. So its "not suspended" is worth one more knock — at most one
    /// per status call, which is every three minutes, and only while the relay
    /// is still saying 403.
    /// </para>
    /// </remarks>
    private void NoteSuspension(bool suspended)
    {
        bool lifted = _poolSawSuspended && !suspended;
        _poolSawSuspended = suspended;
        if (suspended || _connection?.RetryDisabledNow() != true) return;
        Log.Info(lifted
            ? "[pool] ผู้ดูแลยกเลิกการระงับเครื่องนี้แล้ว — กลับมาเชื่อมต่อ relay"
            : "[pool] XMAN Studio ไม่ได้ระงับเครื่องนี้ แต่ relay ยังไม่ให้เข้า — ลองเชื่อมต่อใหม่");
    }

    private bool SameIdentity(string workerId, string token) =>
        string.Equals(Options.WorkerId, workerId, StringComparison.Ordinal)
        && string.Equals(Options.Token, token, StringComparison.Ordinal);

    /// <summary>What one pass read: the freshest answer, the ledger rows it moved, and whether a backlog remains.</summary>
    private sealed record PoolPass(NodeStatus Latest, List<SettledJob> Moved, bool More);

    /// <summary>
    /// Writes the first answer's jobs into the ledger, then follows the cursor
    /// for as long as the website says more changed jobs follow — up to
    /// <see cref="PoolPagesPerPass"/> calls — keeping the newest
    /// <c>updated_at</c> seen as this worker's cursor after every page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A website that ignored the cursor answers with no <c>jobs_more</c>: its
    /// jobs are the latest fifty, recorded exactly as before, and no cursor is
    /// kept — a later website that takes it starts from the beginning.
    /// </para>
    /// <para>
    /// A later page that fails ends the pass with what was read. The cursor
    /// only ever moves past jobs already written, so the next pass picks up
    /// where this one stopped.
    /// </para>
    /// </remarks>
    /// <param name="fresh">No cursor was kept: the start is a guess the first answer may widen.</param>
    /// <returns>Null when the machine was re-paired between two calls.</returns>
    private async Task<PoolPass?> ReadJobPagesAsync(string workerId, string token, NodeStatus first, DateTimeOffset cursor, bool fresh, CancellationToken ct)
    {
        var moved = new List<SettledJob>();
        NodeStatus page = first;

        for (int calls = 1; ; calls++)
        {
            moved.AddRange(RecordSettlements(page));

            if (page.JobsMore is not { } more)
                return new PoolPass(page, moved, More: false);   // the latest fifty, from a website before the cursor

            if (fresh && page.Earnings?.HoldHours is { } hold
                && PoolCursorStart(hold, DateTimeOffset.UtcNow) is var wider && wider < cursor)
            {
                // The start assumed the default hold and the website holds
                // longer. Read again from further back; what this page
                // already wrote is written again as no change.
                cursor = wider;
            }
            else
            {
                DateTimeOffset? newest = page.Jobs?.Max(j => j.UpdatedAt);
                bool advanced = newest > cursor;
                if (advanced) cursor = newest!.Value;
                // Kept even when nothing came back, so a fresh start is pinned
                // rather than sliding forward with the clock past a job that
                // is still inside the website's settle window.
                SavePoolCursor(workerId, cursor);

                if (!more) return new PoolPass(page, moved, More: false);
                if (!advanced)
                {
                    // More, but nothing newer to ask after: asking again would
                    // return this same page for ever.
                    Log.Warn("[pool] XMAN Studio บอกว่ายังมีงานต่อ แต่ไม่ได้ส่ง updated_at ที่ใหม่กว่าเดิมมาให้ถามต่อ — จะถามใหม่รอบหน้า");
                    return new PoolPass(page, moved, More: false);
                }
            }
            fresh = false;

            if (calls >= PoolPagesPerPass) return new PoolPass(page, moved, More: true);

            NodeStatusResult next = await _studio.StatusAsync(workerId, token, cursor, ct);
            if (!SameIdentity(workerId, token)) return null;
            if (next is not { Outcome: StudioOutcome.Ok, Status: { } status })
                return new PoolPass(page, moved, More: true);   // throttled or down mid-backlog: carry on next pass

            page = status;
        }
    }

    /// <summary>Where a worker with no cursor starts reading: one hold and <see cref="PoolCursorMargin"/> back.</summary>
    /// <remarks>
    /// The hold comes from the website and is clamped to a month, so a
    /// nonsense value there cannot overflow the arithmetic or ask for years.
    /// Whole seconds, like the website's own timestamps.
    /// </remarks>
    internal static DateTimeOffset PoolCursorStart(int holdHours, DateTimeOffset now)
    {
        DateTimeOffset start = now.ToUniversalTime() - TimeSpan.FromHours(Math.Clamp(holdHours, 0, 24 * 30)) - PoolCursorMargin;
        return DateTimeOffset.FromUnixTimeSeconds(start.ToUnixTimeSeconds());
    }

    /// <summary>The cursor kept for <paramref name="workerId"/>, or null when there is none for that worker.</summary>
    /// <remarks>
    /// One setting, holding the worker it belongs to: after a re-pairing the
    /// new worker's jobs are a different list, and reading them from the old
    /// worker's cursor would skip everything before it. A value that cannot be
    /// read, or lies absurdly far in the future, is treated as none — the cost
    /// is one fresh start, never a machine that stops seeing its payouts.
    /// </remarks>
    private DateTimeOffset? LoadPoolCursor(string workerId)
    {
        try
        {
            if (Store.GetSetting(PoolCursorSetting) is not { Length: > 0 } json) return null;
            if (System.Text.Json.JsonSerializer.Deserialize<PoolCursor>(json) is not { } saved) return null;
            if (!string.Equals(saved.Worker, workerId, StringComparison.Ordinal)) return null;
            if (saved.After <= 0 || saved.After > DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds()) return null;
            return DateTimeOffset.FromUnixTimeSeconds(saved.After);
        }
        catch (Exception ex)
        {
            Log.Warn($"[warn] อ่านตำแหน่งที่อ่านงานจาก XMAN Studio ไว้ไม่ได้ — เริ่มอ่านใหม่: {ex.Message}");
            return null;
        }
    }

    private void SavePoolCursor(string workerId, DateTimeOffset cursor)
    {
        try
        {
            Store.SetSetting(PoolCursorSetting, System.Text.Json.JsonSerializer.Serialize(new PoolCursor(workerId, cursor.ToUnixTimeSeconds())));
        }
        catch (Exception ex)
        {
            // The next pass reads the same page again; JobSettled writes only what changed.
            Log.Warn($"[warn] จำตำแหน่งที่อ่านงานจาก XMAN Studio ไม่ได้: {ex.Message}");
        }
    }

    /// <param name="Worker">Whose jobs the cursor walks.</param>
    /// <param name="After">Unix seconds: the newest <c>updated_at</c> read so far.</param>
    private sealed record PoolCursor(string Worker, long After);

    /// <summary>
    /// Writes what each of this machine's jobs was settled at into the ledger.
    /// </summary>
    /// <returns>The jobs whose ledger row changed.</returns>
    private List<SettledJob> RecordSettlements(NodeStatus status)
    {
        var moved = new List<SettledJob>();
        if (status.Jobs is not { Count: > 0 } jobs) return moved;

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

        return moved;
    }

    /// <summary>Tells the owner about the ledger rows one pass moved — once, not on every poll or every page.</summary>
    private void SayWhatWasPaid(List<SettledJob> moved, int? hold)
    {
        if (moved.Count == 0) return;

        string parts = string.Join(" · ", moved
            .GroupBy(j => j.Status ?? "?", StringComparer.Ordinal)
            .Select(g => $"{PoolView.DescribePayoutStatus(g.Key == "?" ? null : g.Key, hold)} {g.Count()} งาน {PoolView.Baht(g.Sum(j => j.AmountSatang))}"));
        Log.Info($"[pay] XMAN Studio อัปเดตค่าตอบแทน {moved.Count} งานของเครื่องนี้ — {parts}");
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
        _poolSawSuspended = false;
        _poolLastSaid = null;
        State.SetPool(PoolView.None);
        RefreshPoolStatus();
    }
}
