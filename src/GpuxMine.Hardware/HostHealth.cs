using System.Diagnostics.Eventing.Reader;
using System.Globalization;

using GpuxMine.Node;

namespace GpuxMine.Hardware;

/// <summary>
/// Adds what only the operating system knows to the power ceiling every host
/// can read: whether this machine has been dying under load.
/// </summary>
/// <remarks>
/// <para>
/// Read once per assessment, never in the sensing loop — one process spawn and
/// one event-log query, about a tenth of a second between them, against a
/// measurement that already takes seconds.
/// </para>
/// <para>
/// Neither reading needs administrator rights: <c>nvidia-smi</c> ships with the
/// driver, and the System event log is readable by ordinary users. Anything
/// that does not answer comes back as a zero, which the rest of the client
/// already treats as "not measured" rather than as a real value.
/// </para>
/// </remarks>
public sealed class WindowsHostHealth : NvidiaPowerHealth
{
    /// <summary>
    /// Kernel-Power 41, the event Windows writes on the next boot after it was
    /// never told to shut down.
    /// </summary>
    /// <remarks>
    /// It is logged for power loss, for a held power button and for a hard
    /// reset alike — the event's own <c>BugcheckCode</c> and
    /// <c>PowerButtonTimestamp</c> fields separate those, and both were zero
    /// for the two that prompted this code. Counting all of them is the right
    /// call anyway: every one of them ends a running job without warning.
    /// </remarks>
    private const int UnexpectedShutdownEventId = 41;

    public override HostHealth Read(TimeSpan shutdownWindow)
    {
        var (limit, stock) = ReadPowerLimits();
        var (count, last) = CountUnexpectedShutdowns(shutdownWindow);
        return new HostHealth(limit, stock, count, last);
    }

    /// <summary>How many times this machine lost power without shutting down, and when it last did.</summary>
    private static (int Count, DateTimeOffset? Last) CountUnexpectedShutdowns(TimeSpan window)
    {
        try
        {
            // Filtered by the log itself rather than by walking it: the System
            // log on a machine that has been up for months holds hundreds of
            // thousands of records, and reading them all to find two would cost
            // more than the benchmark.
            long milliseconds = (long)Math.Max(window.TotalMilliseconds, 1);
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[(EventID={UnexpectedShutdownEventId}) and " +
                $"TimeCreated[timediff(@SystemTime)<={milliseconds.ToString(CultureInfo.InvariantCulture)}]]]");

            using var reader = new EventLogReader(query);

            int count = 0;
            DateTimeOffset? last = null;

            while (reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is { } at && (last is null || at > last))
                        last = new DateTimeOffset(at);
                }
            }

            return (count, last);
        }
        catch
        {
            // A locked-down or corrupted event log tells us nothing, which is
            // different from telling us the machine is healthy — but a zero
            // count alongside a zero timestamp is what "nothing" looks like
            // everywhere else in this client.
            return (0, null);
        }
    }
}
