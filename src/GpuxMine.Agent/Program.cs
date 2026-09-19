using GpuxMine.Core;
using GpuxMine.Core.Licensing;
using GpuxMine.Core.Updates;
using GpuxMine.Node;
using Velopack;

// First line of the program, by Velopack's rule: its install, update and
// uninstall hooks are delivered by re-running this executable with special
// arguments, and they must be handled before any other work starts.
VelopackApp.Build().Run();

// Windows consoles still default to a legacy code page, which turns every Thai
// character and every em-dash in this program's output into mojibake. The whole
// UX here is Thai, so this is the difference between readable and unusable.
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (IOException)
{
    // No console attached (a service, or output redirected) — nothing to set.
}

var options = NodeConfiguration.Build(args);

// `--identity`: print what XMAN Studio will see as this machine and exit. The
// first thing support asks for when a device looks duplicated or missing.
if (args.Any(a => a.Equals("--identity", StringComparison.OrdinalIgnoreCase)))
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    string machineId = MachineIdentity.MachineId();
    Console.WriteLine($"machine_id     {machineId}");
    Console.WriteLine($"facts_used     {MachineIdentity.FactCount} (0 = fallback to machine name — id will change if the PC is renamed)");
    Console.WriteLine($"hardware_hash  {MachineIdentity.HardwareHash()}");
    Console.WriteLine($"machine_name   {MachineIdentity.MachineName()}");
    Console.WriteLine($"os_version     {MachineIdentity.OsVersion()}");
    Console.WriteLine($"agent_version  {SelfUpdater.CurrentVersion}");
    Console.WriteLine($"(resolved in {sw.ElapsedMilliseconds} ms)");
    return 0;
}

// Before anything else. Velopack delivers its install/update/uninstall hooks by
// re-running this executable with special arguments, and this also moves our
// working directory out of the install tree — a process sitting inside it is
// exactly what made the BrainX client loop forever trying to update itself.
SelfUpdater.Prepare(options.DataDirectory);

// `--pair CODE`: the headless half of the pairing flow the window has on its
// Settings screen. A rig with no desktop session still has an owner with a
// browser, and without this that owner would have to hand-write agent.json
// from credentials only the relay's admin key can mint.
if (args.FirstOrDefault(a => a.Equals("--pair", StringComparison.OrdinalIgnoreCase)) is not null)
{
    int at = Array.FindIndex(args, a => a.Equals("--pair", StringComparison.OrdinalIgnoreCase));
    string? code = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;

    if (string.IsNullOrWhiteSpace(code))
    {
        Console.WriteLine("ใช้: gpuxmine-agent --pair <รหัสจับคู่>   (ขอรหัสได้ที่หน้า GPUxMINE บน XMAN Studio)");
        return 2;
    }

    Console.WriteLine($"กำลังลงทะเบียนเครื่องกับ {options.XmanStudioUrl}");

    using var pairingHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var studio = new XmanStudioClient(pairingHttp, options.XmanStudioUrl);
    NodeCredentials credentials = await studio.ClaimAsync(code, SelfUpdater.CurrentVersion);

    if (!credentials.Ok || credentials.WorkerId is null || credentials.Token is null)
    {
        Console.WriteLine($"ลงทะเบียนไม่สำเร็จ: {credentials.Message}");
        return 1;
    }

    NodeIdentityFile.Save(options.DataDirectory, credentials.WorkerId, credentials.Token, credentials.RelayUrl);

    Console.WriteLine($"ลงทะเบียนเรียบร้อย — worker {credentials.WorkerId}"
                      + (credentials.Owner is null ? "" : $" ของ {credentials.Owner}"));
    Console.WriteLine($"บันทึกไว้ที่ {NodeIdentityFile.PathIn(options.DataDirectory)}");
    Console.WriteLine("เปิดโปรแกรมอีกครั้งโดยไม่ต้องใส่ --pair เพื่อเริ่มรับงาน");
    return 0;
}

if (!options.Validate(out string error))
{
    Console.Error.WriteLine($"""
        GPUxMINE agent {SelfUpdater.CurrentVersion} — configuration error: {error}

        Enrol a node on the relay, then run:

          gpuxmine-agent --WorkerId gxm-xxxxxxxxxxxx --Token <token> --RelayUrl wss://relay.xman4289.com:8443/agent

        Options:
          --ComfyUrl        where the local ComfyUI listens   (default http://127.0.0.1:8188)
          --Mock true       run a stand-in ComfyUI in-process (no GPU needed; tunnel test only)
          --LicenseKey      Pro Miner licence from XMAN Studio (optional — the free tier earns too)
          --AutoUpdate      false to pin this build
          --XmanStudioUrl   default https://xman4289.com
          --identity        print this machine's identity and exit
          --pair <รหัส>     ลงทะเบียนเครื่องด้วยรหัสจับคู่จากหน้า GPUxMINE บน XMAN Studio

        Settings are also read from agent.json beside the executable, from
        %APPDATA%\GPUxMINE\agent.json, and from GPUXMINE_* environment variables.
        """);
    return 2;
}

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;              // let the node unwind instead of being killed
    stopping.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Cancel();

// Headless has no card sensing of its own yet — that is the Windows-only
// Hardware project, which the WPF app wires in. The Linux build gets an
// nvidia-smi source later. Until then the pool sees this node's presence and
// jobs, just not its temperatures.
// No GPU sensing and no user-activity sensing here — those need Win32, and this
// binary is the one that also runs on Linux. The power ceiling is not Win32
// though: it comes from the driver's own tool, and a headless node that cannot
// report it is a node whose score nobody can explain.
var host = new NodeHost(options, telemetry: null, activity: null,
    alsoLogTo: new ConsoleLog(), health: new NvidiaPowerHealth());
host.UpdateReady += () => stopping.Cancel();

await host.RunAsync(stopping.Token);
await host.DisposeAsync();

// Applied only now, after the relay socket and the runtime are down.
host.ApplyPendingUpdate();

return 0;
