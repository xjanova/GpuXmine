using System.Diagnostics;
using System.Globalization;

namespace GpuxMine.Node;

/// <summary>
/// The part of <see cref="HostHealth"/> any host can answer: how much power
/// the card is allowed to draw, straight from the driver's own tool.
/// </summary>
/// <remarks>
/// <para>
/// This lives here rather than in the Windows sensing project because
/// <c>nvidia-smi</c> is not a platform API — it ships with the driver on
/// Windows and on Linux alike, and starting a process is something every host
/// can do. Putting it behind the Win32 boundary meant the headless agent
/// reported no power ceiling at all, and the very first node measured that way
/// was one running at half its rated watts.
/// </para>
/// <para>
/// Unexplained shutdowns do need the operating system, so they are left at
/// zero here; a host that can read its own boot history derives from this and
/// fills them in.
/// </para>
/// </remarks>
public class NvidiaPowerHealth : IHostHealthSource
{
    public virtual HostHealth Read(TimeSpan shutdownWindow)
    {
        var (limit, stock) = ReadPowerLimits();
        return new HostHealth(limit, stock, 0, null);
    }

    /// <summary>Current and factory power limits in watts, or zeros when nothing answered.</summary>
    /// <remarks>
    /// A telemetry library reports what the card is drawing; only the driver
    /// reports what it is <i>allowed</i> to draw, and the gap between those two
    /// is the whole point. AMD and Intel cards answer nothing here and are
    /// reported as unmeasured rather than as uncapped.
    /// </remarks>
    protected static (int LimitW, int DefaultW) ReadPowerLimits()
    {
        try
        {
            var start = new ProcessStartInfo("nvidia-smi")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--query-gpu=power.limit,power.default_limit");
            start.ArgumentList.Add("--format=csv,noheader,nounits");

            using Process? process = Process.Start(start);
            if (process is null) return (0, 0);

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5_000))
            {
                // A hung nvidia-smi is a driver in trouble, which is worth not
                // waiting on and worth not failing an assessment over.
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (0, 0);
            }

            // One line per GPU; the first is the one ComfyUI reports as device 0.
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return (0, 0);

            string[] fields = lines[0].Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 2) return (0, 0);

            return (Watts(fields[0]), Watts(fields[1]));
        }
        catch
        {
            // No NVIDIA driver, no nvidia-smi on PATH, or a card that does not
            // report limits. None of those is a reason to fail an assessment.
            return (0, 0);
        }

        // "90.00" — always invariant, whatever the owner's regional settings say.
        static int Watts(string field) =>
            double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                ? (int)Math.Round(w)
                : 0;
    }
}
