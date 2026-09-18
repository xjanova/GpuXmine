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

    /// <summary>How many durable facts the id was built from — 0 means the fallback was used.</summary>
    public static int FactCount { get; private set; }

    public static string MachineId()
    {
        if (_cached is not null) return _cached;

        var facts = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            // MachineGuid is written at install time and survives reboots and
            // driver churn; the SMBIOS UUID and board serial tie it to hardware.
            var windows = WindowsFacts();
            AddIfPresent(facts, windows.MachineGuid);
            AddIfPresent(facts, windows.SmbiosUuid);
            AddIfPresent(facts, windows.BoardSerial);
        }
        else
        {
            // systemd's machine id is the closest equivalent and survives reboots.
            AddIfPresent(facts, ReadFirstLine("/etc/machine-id"));
            AddIfPresent(facts, ReadFirstLine("/var/lib/dbus/machine-id"));
            AddIfPresent(facts, ReadFirstLine("/sys/class/dmi/id/product_uuid"));
        }

        FactCount = facts.Count;

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
            OperatingSystem.IsWindows() ? WindowsFacts().ProcessorId ?? "" : ReadFirstLine("/proc/cpuinfo") ?? "",
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

    private sealed record WindowsMachineFacts(string? MachineGuid, string? SmbiosUuid, string? BoardSerial, string? ProcessorId);

    private static WindowsMachineFacts? _windowsFacts;

    /// <summary>
    /// One PowerShell round-trip for every Windows fact we use.
    /// </summary>
    /// <remarks>
    /// Not <c>wmic.exe</c>: it is gone from Windows 11 24H2 onwards (verified on
    /// the machine this was written on), and an agent whose identity quietly
    /// degrades on newer Windows is exactly the kind of bug nobody notices
    /// until the device registry fills with duplicates. CIM is what replaced
    /// it. One process for all four values because each spawn costs ~700 ms;
    /// the result is cached for the life of the process anyway.
    ///
    /// Shelled out rather than referencing Microsoft.Win32.Registry or
    /// System.Management, so this project stays platform-neutral — the Linux
    /// agent compiles from exactly these sources.
    /// </remarks>
    private static WindowsMachineFacts WindowsFacts()
    {
        if (_windowsFacts is not null) return _windowsFacts;

        const string script =
            "$ErrorActionPreference='SilentlyContinue';" +
            "(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Cryptography').MachineGuid;" +
            "(Get-CimInstance Win32_ComputerSystemProduct).UUID;" +
            "(Get-CimInstance Win32_BaseBoard).SerialNumber;" +
            "(Get-CimInstance Win32_Processor | Select-Object -First 1).ProcessorId";

        string? output = Run("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
            timeoutMs: 8000);

        string?[] lines = (output ?? string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .ToArray();

        // Values that mean "unknown" in SMBIOS, which are not facts about this
        // machine and would make every such machine look identical.
        static string? Real(string? v) =>
            v is null || v.Length == 0
            || v.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase)
            || v.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            || v.Equals("None", StringComparison.OrdinalIgnoreCase)
            || v.All(c => c == '0' || c == 'F' || c == 'f' || c == '-')
                ? null
                : v;

        _windowsFacts = new WindowsMachineFacts(
            MachineGuid: Real(lines.ElementAtOrDefault(0)),
            SmbiosUuid: Real(lines.ElementAtOrDefault(1)),
            BoardSerial: Real(lines.ElementAtOrDefault(2)),
            ProcessorId: Real(lines.ElementAtOrDefault(3)));

        return _windowsFacts;
    }

    private static string? Run(string fileName, string arguments, int timeoutMs = 3000)
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
            if (!process.WaitForExit(timeoutMs))
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
