namespace GpuxMine.Node;

/// <summary>
/// What a stretch of finished work adds up to.
/// </summary>
/// <remarks>
/// Counted by the database over the same window the list is showing, rather
/// than summed from the rows on screen — the list is capped at a few hundred
/// rows and the totals must not be.
/// </remarks>
public sealed class JobTotals
{
    public int Jobs { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }

    /// <summary>Integer satang, settled and not voided. Unsettled payouts count as nothing, never as zero baht earned.</summary>
    public long PayoutSatang { get; init; }

    /// <summary>Time the card actually spent rendering, summed from each job.</summary>
    public TimeSpan BusyTime { get; init; }

    /// <summary>Jobs the pool has settled (and not voided) — what <see cref="PayoutSatang"/> is the sum of.</summary>
    public int Settled { get; init; }

    /// <summary>Completed jobs the pool has not settled yet.</summary>
    public int Unsettled { get; init; }

    /// <summary>What the settled free-share jobs would have paid. Integer satang.</summary>
    public long DonatedSatang { get; init; }

    public decimal PayoutThb => PayoutSatang / 100m;
}

/// <summary>
/// Counts and settled money since a moment — the Dashboard's "today".
/// </summary>
/// <param name="EarnedSatang">Settled, not voided. Null when the pool has settled nothing in the window — shown as "—", never as 0.</param>
/// <param name="Unsettled">Completed jobs the pool has not settled yet.</param>
/// <param name="Settled">Jobs the pool has settled and not voided.</param>
/// <param name="DonatedSatang">What the settled free-share jobs would have paid.</param>
public sealed record LedgerTotals(int Completed, int Failed, long? EarnedSatang, int Unsettled, int Settled, long DonatedSatang);
