namespace GpuxMine.Node;

/// <summary>
/// The switches the node passes to itself across a relaunch, and how they are
/// read back.
/// </summary>
/// <remarks>
/// <para>
/// The same command line is also read by the configuration builder, which
/// takes <c>--key value</c> pairs: a bare switch placed before a real setting
/// swallows that setting as its "value" — <c>--after-restart --DataDirectory X</c>
/// loses the data directory. So the switches are always appended after
/// everything else, and the relaunch carries the owner's own arguments
/// through unchanged: an update used to restart the program with none at all,
/// which dropped a <c>--DataDirectory</c> on the floor.
/// </para>
/// </remarks>
public static class LaunchFlags
{
    /// <summary>Start to the tray, with no window and no splash.</summary>
    public const string Minimized = "--minimized";

    /// <summary>
    /// This process was started by the one before it — an update, a restart —
    /// which may still be on its way out. Wait for its lock instead of
    /// deciding a second copy is running and exiting.
    /// </summary>
    public const string AfterRestart = "--after-restart";

    /// <summary>How long a relaunched copy waits for the old one to let go.</summary>
    public static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(30);

    /// <summary>The switch is present, bare or as <c>--switch=true</c>.</summary>
    public static bool Has(IEnumerable<string> args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase)
                      || a.Equals(flag + "=true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The arguments for the next launch: the owner's own, without the
    /// switches this class manages, then <paramref name="add"/> at the end.
    /// </summary>
    public static string[] ForRestart(IEnumerable<string> original, params string[] add)
    {
        var kept = original
            .Where(a => !IsManaged(a))
            .ToList();
        kept.AddRange(add);
        return [.. kept];
    }

    private static bool IsManaged(string arg) =>
        arg.Equals(Minimized, StringComparison.OrdinalIgnoreCase)
        || arg.Equals(Minimized + "=true", StringComparison.OrdinalIgnoreCase)
        || arg.Equals(AfterRestart, StringComparison.OrdinalIgnoreCase)
        || arg.Equals(AfterRestart + "=true", StringComparison.OrdinalIgnoreCase);
}
