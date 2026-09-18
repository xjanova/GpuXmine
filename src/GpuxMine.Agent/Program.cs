using GpuxMine.Core;
using GpuxMine.Core.Updates;
using GpuxMine.Node;
using Microsoft.Extensions.Configuration;
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

IConfigurationRoot configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("agent.json", optional: true, reloadOnChange: false)
    .AddJsonFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpuxMine", "agent.json"),
        optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("GPUXMINE_")
    .AddCommandLine(args)
    .Build();

var options = configuration.Get<NodeOptions>() ?? new NodeOptions();

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

if (!options.Validate(out string error))
{
    Console.Error.WriteLine($"""
        GPUxMINE agent {SelfUpdater.CurrentVersion} — configuration error: {error}

        Enrol a node on the relay, then run:

          gpuxmine-agent --WorkerId gxm-xxxxxxxxxxxx --Token <token> --RelayUrl ws://localhost:5080/agent

        Options:
          --ComfyUrl        where the local ComfyUI listens   (default http://127.0.0.1:8188)
          --Mock true       run a stand-in ComfyUI in-process (no GPU needed; tunnel test only)
          --LicenseKey      Pro Miner licence from XMAN Studio (optional — the free tier earns too)
          --AutoUpdate      false to pin this build
          --XmanStudioUrl   default https://xman4289.com
          --identity        print this machine's identity and exit

        Settings are also read from agent.json beside the executable, from
        %LOCALAPPDATA%\GpuxMine\agent.json, and from GPUXMINE_* environment variables.
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
var host = new NodeHost(options, telemetry: null, activity: null, alsoLogTo: new ConsoleLog());
host.UpdateReady += () => stopping.Cancel();

await host.RunAsync(stopping.Token);
await host.DisposeAsync();

// Applied only now, after the relay socket and the runtime are down.
host.ApplyPendingUpdate();

return 0;
