using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace GpuxMine.Core;

/// <summary>
/// A stable identifier for this machine, for XMAN Studio's device registry.
/// </summary>
/// <remarks>
/// The server takes a `machine_id` of 32–64 characters, so this is a SHA-256
/// hex digest of whatever durable machine facts the OS will give us.
///
/// "Durable" is doing real work here. The id must survive a reboot, a driver
/// update and an agent reinstall, or every node in the fleet looks like a new
/// device every week and the licence-per-machine accounting becomes nonsense.
/// It must also not be trivially forgeable, because the same identity is what
/// stops one person registering fifty workers off one PC.
///
/// Nothing here is a security boundary on its own — a determined owner can
/// spoof any of it. It raises the cost of a fake fleet; the benchmark and the
/// GPU UUID (M3) are what make the fleet actually have to exist.
/// </remarks>
public static class MachineIdentity
{
    private static string? _cached;

    public static string MachineId()
    {
        if (_cached is not null) return _cached;

        var facts = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            // Written at install time and kept across reboots and driver churn.
            AddIfPresent(facts, ReadRegistry(@"HKLM\SOFTWARE\Microsoft\Cryptography", "MachineGuid"));
            AddIfPresent(facts, Wmic("csproduct", "uuid"));
            AddIfPresent(facts, Wmic("baseboard", "serialnumber"));
        }
        else
        {
            // systemd's machine id is the closest equivalent and survives reboots.
            AddIfPresent(facts, ReadFirstLine("/etc/machine-id"));
            AddIfPresent(facts, ReadFirstLine("/var/lib/dbus/machine-id"));
            AddIfPresent(facts, ReadFirstLine("/sys/class/dmi/id/product_uuid"));
        }

        // A machine that gives us nothing would otherwise hash to the same id as
        // every other such machine, quietly merging them into one device.
        if (facts.Count == 0)
        {
            facts.Add("fallback:" + Environment.MachineName + ":" + Environment.UserName);
        }

        _cached = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', facts))))
            .ToLowerInvariant();

        return _cached;
    }

    /// <summary>A coarser hash of the hardware itself, for spotting a machine that was re-imaged.</summary>
    public static string HardwareHash()
    {
        string raw = string.Join('|', [
            Environment.ProcessorCount.ToString(),
            RuntimeInformationSafe(),
            OperatingSystem.IsWindows() ? Wmic("cpu", "processorid") ?? "" : ReadFirstLine("/proc/cpuinfo") ?? "",
        ]);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    public static string MachineName() => Environment.MachineName;

    public static string OsVersion() => Environment.OSVersion.VersionString;

    private static string RuntimeInformationSafe()
        => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();

    private static void AddIfPresent(List<string> into, string? value)
    {
        value = value?.Trim();
        if (!string.IsNullOrEmpty(value)) into.Add(value);
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadRegistry(string key, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;

        // Shelled out rather than referencing Microsoft.Win32.Registry so this
        // project stays platform-neutral — the Linux agent (M3) compiles from
        // exactly these sources.
        string? output = Run("reg.exe", $"query \"{key}\" /v {name}");
        if (output is null) return null;

        foreach (string line in output.Split('\n'))
        {
            int marker = line.IndexOf("REG_SZ", StringComparison.Ordinal);
            if (marker >= 0) return line[(marker + "REG_SZ".Length)..].Trim();
        }
        return null;
    }

    private static string? Wmic(string alias, string property)
    {
        string? output = Run("wmic.exe", $"{alias} get {property}");
        if (output is null) return null;

        // First line is the column header, which is not a fact about this machine.
        return output.Split('\n')
            .Skip(1)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
    }

    private static string? Run(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;

            string output = process.StandardOutput.ReadToEnd();

            // Never block startup on a hung helper: identity is worth a couple of
            // seconds, not a launch that appears to hang.
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
