using GpuxMine.Node;
using LibreHardwareMonitor.Hardware;

namespace GpuxMine.Hardware;

/// <summary>
/// The card's vital signs, through LibreHardwareMonitor — the same library
/// every hardware monitor on Windows ends up using, which is the whole reason
/// this client is C#.
/// </summary>
/// <remarks>
/// GPU sensors come from the vendor APIs (NVAPI / ADL) and need no driver of
/// our own and no administrator rights. The library's CPU and motherboard
/// sensors do need its kernel driver, so they are switched off here: a node
/// must never ask for elevation just to draw a temperature.
///
/// Reads run on the caller's thread and take a few milliseconds; the host
/// calls this every 1.4 s off the UI thread.
/// </remarks>
public sealed class GpuTelemetry : ITelemetrySource
{
    private readonly Computer _computer;
    private readonly Lock _gate = new();
    private IHardware? _gpu;
    private bool _opened;
    private string? _driver;

    public GpuTelemetry()
    {
        _computer = new Computer
        {
            IsGpuEnabled = true,
            IsCpuEnabled = false,
            IsMotherboardEnabled = false,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
        };
    }

    public GpuSnapshot Read()
    {
        lock (_gate)
        {
            if (!_opened)
            {
                try
                {
                    _computer.Open();
                    _opened = true;
                }
                catch (Exception)
                {
                    return GpuSnapshot.None;
                }
                _gpu = PickGpu();
                _driver = ReadDriverVersion();
            }

            if (_gpu is null) return GpuSnapshot.None;

            try
            {
                _gpu.Update();
            }
            catch
            {
                return GpuSnapshot.None with { GpuName = _gpu.Name };
            }

            float load = 0, temp = 0, fan = 0, power = 0, memUsed = 0, memTotal = 0;

            foreach (var s in _gpu.Sensors)
            {
                if (s.Value is not { } v) continue;
                switch (s.SensorType)
                {
                    case SensorType.Load when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase):
                        load = v; break;
                    case SensorType.Temperature when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase):
                        temp = v; break;
                    case SensorType.Control when s.Name.Contains("Fan", StringComparison.OrdinalIgnoreCase):
                        fan = v; break;
                    case SensorType.Power when s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                                             || s.Name.Contains("Power", StringComparison.OrdinalIgnoreCase)
                                             || s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase):
                        if (power == 0) power = v; break;
                    case SensorType.SmallData when s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase):
                        memUsed = v; break;
                    case SensorType.SmallData when s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase):
                        memTotal = v; break;
                }
            }

            // Some cards report the fan only as a percentage under Control,
            // others only as RPM under Fan; a percentage is what the gauge wants.
            if (fan == 0)
            {
                var rpm = _gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan)?.Value;
                if (rpm is > 0) fan = Math.Min(100, rpm.Value / 30f);   // rough: ~3000 rpm ≈ 100 %
            }

            return new GpuSnapshot(
                GpuName: _gpu.Name,
                Driver: _driver,
                VramTotalMb: (int)memTotal,
                VramUsedMb: (int)memUsed,
                LoadPct: (int)Math.Round(load),
                TempC: (int)Math.Round(temp),
                FanPct: (int)Math.Round(fan),
                PowerW: (int)Math.Round(power),
                Measured: true);
        }
    }

    private IHardware? PickGpu()
    {
        // A laptop has an integrated GPU too; the discrete one is the one that
        // earns. Prefer NVIDIA, then AMD, then whatever is left.
        var gpus = _computer.Hardware
            .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .ToList();

        return gpus.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
            ?? gpus.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd)
            ?? gpus.FirstOrDefault();
    }

    private static string? ReadDriverVersion()
    {
        // nvidia-smi is installed with every NVIDIA driver and answers in ~100 ms.
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("nvidia-smi",
                "--query-gpu=driver_version --format=csv,noheader")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            string line = p.StandardOutput.ReadLine()?.Trim() ?? "";
            p.WaitForExit(2000);
            return line.Length > 0 ? line : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_opened)
            {
                try { _computer.Close(); } catch { /* driver already gone */ }
                _opened = false;
            }
        }
    }
}
