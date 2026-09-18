using GpuxMine.Agent;
using GpuxMine.Core.Licensing;
using GpuxMine.Core.Updates;
using Microsoft.Extensions.Configuration;

var log = new ConsoleLog();

IConfigurationRoot configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("agent.json", optional: true, reloadOnChange: false)
    .AddJsonFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpuxMine", "agent.json"),
        optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("GPUXMINE_")
    .AddCommandLine(args)
    .Build();

var options = configuration.Get<AgentOptions>() ?? new AgentOptions();

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

        Settings are also read from agent.json beside the executable, from
        %LOCALAPPDATA%\GpuxMine\agent.json, and from GPUXMINE_* environment variables.
        """);
    return 2;
}

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;              // let the agent unwind instead of being killed
    stopping.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Cancel();

MockComfy? mock = null;
if (options.Mock)
{
    int port = new Uri(options.ComfyUrl).Port;
    mock = new MockComfy(port, log);
    mock.Start(stopping.Token);
}

await using var runtime = new ComfyRuntime(options, log);
var connection = new RelayConnection(options, runtime, log);

log.Info($"GPUxMINE agent {SelfUpdater.CurrentVersion} starting — worker {options.WorkerId}, " +
         $"runtime {options.ComfyUrl}{(options.Mock ? " (mock)" : "")}");

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var studio = new XmanStudioClient(httpClient, options.XmanStudioUrl);
var updater = new SelfUpdater(options.UpdateRepo, options.DataDirectory, log.Warn);
updater.NoteVersionRunning();

// XMAN Studio is the account system, not a gate on running. Everything below
// reports and moves on: a node whose owner has no licence still earns on the
// free tier, and a website outage must not idle the fleet.
_ = Task.Run(async () =>
{
    var registration = await studio.RegisterDeviceAsync(SelfUpdater.CurrentVersion, stopping.Token);
    log.Info(registration.Ok
        ? "registered with XMAN Studio"
        : $"could not register with XMAN Studio: {registration.Message}");

    LicenseState license = await studio.ValidateAsync(options.LicenseKey, stopping.Token);
    log.Info(license.Valid
        ? $"licence active — {license.Plan ?? "pro"}{(license.ExpiresAt is { } e ? $", ถึง {e:yyyy-MM-dd}" : "")}"
        : $"running on the free tier ({license.Message ?? license.Status})");
}, stopping.Token);

// Progress tracking must not be able to take the agent down: a node that cannot
// report a percentage is still a node that can render.
Task progress = Task.Run(async () =>
{
    try { await runtime.TrackProgressAsync(stopping.Token); }
    catch (Exception ex) { log.Warn($"progress tracker stopped: {ex.Message}"); }
}, stopping.Token);

Task updates = options.AutoUpdate
    ? Task.Run(() => UpdateLoopAsync(updater, studio, runtime, options, log, stopping), stopping.Token)
    : Task.CompletedTask;

try
{
    await connection.RunForeverAsync(stopping.Token);
}
finally
{
    await stopping.CancelAsync();
    await Task.WhenAny(Task.WhenAll(progress, updates), Task.Delay(TimeSpan.FromSeconds(2)));
    mock?.Dispose();
    log.Info("agent stopped");
}

return 0;

static async Task UpdateLoopAsync(
    SelfUpdater updater,
    XmanStudioClient studio,
    ComfyRuntime runtime,
    AgentOptions options,
    ILoggerish log,
    CancellationTokenSource stopping)
{
    var period = TimeSpan.FromHours(Math.Max(1, options.UpdateCheckHours));

    // A little breathing room after launch so the first check never competes
    // with connecting to the relay and claiming the first job.
    try { await Task.Delay(TimeSpan.FromSeconds(45), stopping.Token); }
    catch (OperationCanceledException) { return; }

    while (!stopping.IsCancellationRequested)
    {
        // Ask XMAN Studio too, not just the release feed: it is the only channel
        // that can say "this build must not keep running", which is what a
        // security fix needs to reach a fleet of machines we do not own.
        var studioView = await studio.CheckUpdateAsync(SelfUpdater.CurrentVersion, options.LicenseKey, stopping.Token);
        if (studioView?.ForceUpdate == true)
        {
            log.Warn($"XMAN Studio ระบุว่าเวอร์ชันนี้ใช้ต่อไม่ได้ — ต้องอัปเดตเป็น {studioView.Latest}");
        }

        UpdateCheck check = await updater.CheckAsync(stopping.Token);
        switch (check.Outcome)
        {
            case UpdateOutcome.NotInstalled:
                log.Info(check.Detail ?? "not installed — skipping auto-update");
                return; // never changes within a run

            case UpdateOutcome.GaveUp:
                log.Warn(check.Detail ?? "update could not be applied");
                return; // looping harder is exactly what went wrong for BrainX

            case UpdateOutcome.Downloaded:
                log.Info($"update {check.AvailableVersion} downloaded — waiting for the node to go idle");

                // Never mid-render. Applying restarts the process, and the
                // customer's job would die with it.
                while (runtime.IsBusy && !stopping.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(15), stopping.Token); }
                    catch (OperationCanceledException) { return; }
                }
                if (stopping.IsCancellationRequested) return;

                // Shut the runtime and the relay socket down first. A Python
                // child still running out of the install tree holds the very
                // directory Velopack has to rename.
                log.Info("stopping the local runtime before applying the update");
                await stopping.CancelAsync();
                await Task.Delay(TimeSpan.FromSeconds(2));

                updater.ApplyAndRestart();
                return;

            case UpdateOutcome.Failed:
            case UpdateOutcome.UpToDate:
            default:
                break;
        }

        try { await Task.Delay(period, stopping.Token); }
        catch (OperationCanceledException) { return; }
    }
}
