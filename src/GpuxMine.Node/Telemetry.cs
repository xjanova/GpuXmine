namespace GpuxMine.Node;

/// <summary>One reading of the card. Zeros mean "not measured", and the UI says so rather than drawing a 0% gauge.</summary>
public sealed record GpuSnapshot(
    string? GpuName,
    string? Driver,
    int VramTotalMb,
    int VramUsedMb,
    int LoadPct,
    int TempC,
    int FanPct,
    int PowerW,
    bool Measured)
{
    public static readonly GpuSnapshot None = new(null, null, 0, 0, 0, 0, 0, 0, Measured: false);
}

/// <summary>Where the numbers come from. Windows reads the card through LibreHardwareMonitor; other hosts plug in their own.</summary>
public interface ITelemetrySource : IDisposable
{
    GpuSnapshot Read();
}

/// <summary>Whether the owner is using the machine — the "yield when I use the PC" signal.</summary>
public interface IUserActivitySource
{
    /// <returns>null when it cannot tell, so the caller can decide what to assume.</returns>
    bool? IsUserActive();

    /// <summary>A fullscreen game or presentation. Stronger than "typed recently": never share on top of this.</summary>
    bool? IsFullscreenApp();
}

/// <summary>
/// What the machine knows about itself that no benchmark can see: how much
/// power the card is allowed to draw, and whether the host has been losing
/// power under load.
/// </summary>
/// <remarks>
/// <para>
/// Both of these came out of one night, 2026-09-18, written up in
/// <c>docs/INCIDENT-2026-09-18-host-power-loss.md</c>. The card this client was
/// built on reported a 90 W limit against its 180 W stock rating — so every
/// timing ever taken on it is a half-power figure, and calling any of them
/// "what a 1070 Ti does" was wrong. The same night that host died twice under
/// a sustained render, thirty-one minutes apart, with Kernel-Power 41, no
/// bugcheck and no WHEA record: instant power loss, not a crash.
/// </para>
/// <para>
/// A node that does that in the middle of a customer's job loses the job, the
/// customer's wait and the owner's payout — and nothing anywhere would have
/// known why. Two numbers, read once per assessment, are what make it visible.
/// </para>
/// </remarks>
/// <param name="PowerLimitW">Watts the card is currently allowed to draw. 0 when it could not be read.</param>
/// <param name="PowerDefaultW">Watts the card is rated for from the factory. 0 when it could not be read.</param>
/// <param name="HardShutdowns">Power losses with no clean shutdown, inside the window asked for.</param>
/// <param name="LastHardShutdown">When the most recent one was, for the owner to match against their own memory.</param>
public sealed record HostHealth(
    int PowerLimitW,
    int PowerDefaultW,
    int HardShutdowns,
    DateTimeOffset? LastHardShutdown)
{
    public static readonly HostHealth Unknown = new(0, 0, 0, null);

    /// <summary>
    /// The card is held below what it was built to draw, so its timings are not
    /// this model's timings.
    /// </summary>
    /// <remarks>
    /// A few watts below stock is normal vendor variation. Half is somebody
    /// working around a power supply that could not hold the card up.
    /// </remarks>
    public bool PowerCapped =>
        PowerLimitW > 0 && PowerDefaultW > 0 && PowerLimitW < PowerDefaultW * 0.95;

    /// <summary>Share of the factory power rating this card is allowed, as a percentage.</summary>
    public int PowerPct =>
        PowerLimitW > 0 && PowerDefaultW > 0
            ? (int)Math.Round(PowerLimitW * 100.0 / PowerDefaultW)
            : 0;
}

/// <summary>
/// Reads <see cref="HostHealth"/>. Windows goes to the driver and the event
/// log; other hosts bring their own, and a host with none says so.
/// </summary>
public interface IHostHealthSource
{
    /// <param name="shutdownWindow">How far back to count unexpected power losses.</param>
    HostHealth Read(TimeSpan shutdownWindow);
}

/// <summary>For hosts with no sensing (headless Linux until its own source exists, tests).</summary>
public sealed class NullTelemetry : ITelemetrySource, IUserActivitySource, IHostHealthSource
{
    public GpuSnapshot Read() => GpuSnapshot.None;
    public bool? IsUserActive() => null;
    public bool? IsFullscreenApp() => null;
    public HostHealth Read(TimeSpan shutdownWindow) => HostHealth.Unknown;
    public void Dispose() { }
}
