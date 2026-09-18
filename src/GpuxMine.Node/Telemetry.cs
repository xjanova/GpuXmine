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

/// <summary>For hosts with no sensing (headless Linux until its own source exists, tests).</summary>
public sealed class NullTelemetry : ITelemetrySource, IUserActivitySource
{
    public GpuSnapshot Read() => GpuSnapshot.None;
    public bool? IsUserActive() => null;
    public bool? IsFullscreenApp() => null;
    public void Dispose() { }
}
