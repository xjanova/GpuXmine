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

    /// <summary>Integer satang. Null payouts count as nothing, never as zero baht earned.</summary>
    public long PayoutSatang { get; init; }

    /// <summary>Time the card actually spent rendering, summed from each job.</summary>
    public TimeSpan BusyTime { get; init; }

    public decimal PayoutThb => PayoutSatang / 100m;
}
