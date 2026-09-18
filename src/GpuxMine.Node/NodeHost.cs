using GpuxMine.Core.Licensing;
using GpuxMine.Core.Updates;
using GpuxMine.Protocol;

namespace GpuxMine.Node;

/// <summary>
/// One node, end to end: settings, sensing, the local runtime, the relay
/// connection, licensing and self-update. The console agent and the WPF app
/// are both thin hosts around this.
/// </summary>
/// <remarks>
/// Two lifetimes live here and must not be confused:
/// <list type="bullet">
///   <item><b>The host</b> — from construction to <see cref="DisposeAsync"/>.
///     Sensing, the progress tracker, licence and update checks run for the
///     whole of it.</item>
///   <item><b>A session</b> — from <see cref="StartAsync"/> to
///     <see cref="StopAsync"/>: the relay connection. The owner's START/STOP
///     dial. Stopping never kills a render in flight; it stops taking new
///     work and lets the relay socket close.</item>
/// </list>
/// </remarks>
public sealed class NodeHost : IAsyncDisposable
{
    /// <summary>The prototype's telemetry cadence. Fast enough to feel live, slow enough to cost nothing.</summary>
    public static readonly TimeSpan SensePeriod = TimeSpan.FromMilliseconds(1400);

    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(120);

    public NodeOptions Options { get; }
    public NodeSettings Settings { get; }
    public NodeState State { get; } = new();
    public ActivityLog Log { get; }
    public JobHistory Jobs { get; }
    public ComfyRuntime Runtime { get; }
    public SelfUpdater Updater { get; }
    public Storage.NodeStore Store { get; }

    private readonly ITelemetrySource _telemetry;
    private readonly IUserActivitySource _activity;
    private readonly XmanStudioClient _studio;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource _hostCts = new();
    private readonly List<Task> _background = [];

    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;
    private MockComfy? _mock;
    private DateTimeOffset _lastUserInput = DateTimeOffset.MinValue;

    /// <summary>Set once an update is staged and the node is idle; the host applies it on shutdown.</summary>
    public bool UpdatePending { get; private set; }

    public NodeHost(
        NodeOptions options,
        ITelemetrySource? telemetry = null,
        IUserActivitySource? activity = null,
        ILoggerish? alsoLogTo = null)
    {
        Options = options;
        Store = new Storage.NodeStore(Path.Combine(options.DataDirectory, "node.db"));
        NodeSettings.MigrateLegacyFile(options.DataDirectory, Store);
        Settings = NodeSettings.Load(Store);
        Log = new ActivityLog(Store, alsoLogTo);
        Jobs = new JobHistory(Store);

        var nothing = new NullTelemetry();
        _telemetry = telemetry ?? nothing;
        _activity = activity ?? nothing;

        Runtime = new ComfyRuntime(options, Log, Decide);
        Runtime.Job += OnJobEvent;

        _studio = new XmanStudioClient(_http, options.XmanStudioUrl);
        Updater = new SelfUpdater(options.UpdateRepo, options.DataDirectory, Log.Warn);
    }

    // ------------------------------------------------------------- lifetime

    /// <summary>Starts sensing and the background checks. Does not connect to the relay.</summary>
    public void Begin()
    {
        Updater.NoteVersionRunning();
        Log.Info($"[cfg] GPUxMINE {SelfUpdater.CurrentVersion} — worker {Options.WorkerId}, runtime {Options.ComfyUrl}{(Options.Mock ? " (mock)" : "")}");

        if (Options.Mock)
        {
            _mock = new MockComfy(new Uri(Options.ComfyUrl).Port, Log);
            _mock.Start(_hostCts.Token);
        }

        _background.Add(Guard(Runtime.TrackProgressAsync(_hostCts.Token), "progress tracker"));
        _background.Add(Guard(SenseLoopAsync(_hostCts.Token), "sensing"));
        _background.Add(Guard(StudioLoopAsync(_hostCts.Token), "xman studio"));
        _background.Add(Guard(SweepLoopAsync(_hostCts.Token), "database sweep"));
        _background.Add(Guard(ReconcileLoopAsync(_hostCts.Token), "ledger reconcile"));
        if (Options.AutoUpdate)
            _background.Add(Guard(UpdateLoopAsync(_hostCts.Token), "updates"));
    }

    /// <summary>The owner's START. Idempotent: pressing it twice does not open two connections.</summary>
    public Task StartAsync()
    {
        if (_sessionCts is not null) return Task.CompletedTask;

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_hostCts.Token);
        var connection = new RelayConnection(Options, Runtime, Log, HeartbeatPayload);
        connection.ConnectedChanged += up =>
            State.SetConnection(up ? ConnectionState.Connected : (State.Running ? ConnectionState.Reconnecting : ConnectionState.Stopped));

        State.SetRunning(true);
        State.SetConnection(ConnectionState.Connecting);
        Log.Info("[net] sharing started");

        _sessionTask = Guard(connection.RunForeverAsync(_sessionCts.Token), "relay session");
        return Task.CompletedTask;
    }

    /// <summary>The owner's STOP. The running render, if any, is left to finish.</summary>
    public async Task StopAsync()
    {
        var cts = _sessionCts;
        if (cts is null) return;
        _sessionCts = null;

        Log.Info("[net] sharing stopped by owner");
        await cts.CancelAsync();
        if (_sessionTask is not null)
            await Task.WhenAny(_sessionTask, Task.Delay(TimeSpan.FromSeconds(5)));
        cts.Dispose();

        State.SetRunning(false);
        State.SetConnection(ConnectionState.Stopped);
    }

    /// <summary>Headless: start, run until cancelled, stop.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Begin();
        await StartAsync();
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { /* asked to stop */ }
        await StopAsync();
    }

    public void SaveSettings()
    {
        try
        {
            // Quiet on success. Every slider tick and screen change lands here,
            // and "settings saved" six times in ten seconds is the log telling
            // the owner nothing about their node.
            Settings.Save(Store);
            Reevaluate();
        }
        catch (Exception ex)
        {
            Log.Warn($"[cfg] could not save settings: {ex.Message}");
        }
    }

    // ------------------------------------------------------------- decisions

    /// <summary>Would this node take a new job this second, and if not, why.</summary>
    private AcceptDecision Decide()
    {
        if (!State.Running) return new AcceptDecision(false, "หยุดแชร์อยู่");

        if (Settings.ScheduleOnly && !Settings.IsScheduledNow(DateTime.Now))
            return new AcceptDecision(false, "นอกตารางเวลาแชร์");

        if (Settings.YieldWhenActive)
        {
            if (_activity.IsFullscreenApp() == true)
                return new AcceptDecision(false, "มีโปรแกรมเต็มจอ (เกม/นำเสนอ) กำลังทำงาน");

            bool? active = _activity.IsUserActive();
            if (active == true || (active is null && DateTimeOffset.Now - _lastUserInput < IdleGrace))
                return new AcceptDecision(false, "เจ้าของกำลังใช้เครื่อง");
        }

        var gpu = State.Gpu;
        if (gpu.Measured && gpu.TempC >= Settings.TempCeilingC)
            return new AcceptDecision(false, $"อุณหภูมิ {gpu.TempC}°C ถึงเพดาน {Settings.TempCeilingC}°C");

        return AcceptDecision.Yes;
    }

    private void Reevaluate()
    {
        var d = Decide();
        if (d.Accept != State.Accepting || d.Reason != State.PauseReason)
        {
            State.SetAccepting(d.Accept, d.Reason);
            if (State.Running)
                Log.Info(d.Accept ? "[auto] accepting jobs" : $"[auto] paused — {d.Reason}");
        }
    }

    private AgentTelemetry HeartbeatPayload()
    {
        var g = State.Gpu;
        return new AgentTelemetry
        {
            GpuName = g.GpuName,
            VramTotalMb = g.VramTotalMb,
            VramUsedMb = g.VramUsedMb,
            GpuLoadPct = g.LoadPct,
            TempC = g.TempC,
            PowerW = g.PowerW,
            Accepting = State.Accepting,
            FreeSharePct = Settings.FreeSharePercent,
        };
    }

    // ------------------------------------------------------------- loops

    private async Task SenseLoopAsync(CancellationToken ct)
    {
        var today = DateTimeOffset.Now.Date;
        while (!ct.IsCancellationRequested)
        {
            GpuSnapshot snapshot;
            try { snapshot = _telemetry.Read(); }
            catch (Exception ex) { Log.Warn($"[gpu] read failed: {ex.Message}"); snapshot = GpuSnapshot.None; }
            State.SetGpu(snapshot);

            if (_activity.IsUserActive() == true) _lastUserInput = DateTimeOffset.Now;

            Reevaluate();

            // Counted in SQL from midnight, so the figure survives a restart
            // and cannot drift from the rows the queue screen is showing.
            var midnight = new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset);
            var (ok, failed, earned) = Store.Totals(midnight);
            State.SetCounts(ok, failed);
            State.SetEarnedToday(earned);
            State.SetCurrentJob(Jobs.Current);

            // A day boundary crossed while running: re-read so "today" means today.
            if (today != DateTimeOffset.Now.Date) today = DateTimeOffset.Now.Date;

            try { await Task.Delay(SensePeriod, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Settles jobs the websocket never reported, by asking ComfyUI's history.
    /// </summary>
    /// <remarks>
    /// The socket drops on every ComfyUI restart, and a fully-cached prompt can
    /// complete without emitting the events we listen for. Either way the row
    /// would sit at "queued" forever, and work the node actually did would
    /// never reach the owner's count — so the ledger is reconciled against the
    /// one source that cannot miss an event.
    /// </remarks>
    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
            catch (OperationCanceledException) { return; }

            foreach (string promptId in Store.UnsettledJobs(TimeSpan.FromSeconds(15)))
            {
                var (done, success, filename, error) = await Runtime.QueryHistoryAsync(promptId, ct);
                if (!done) continue;

                Store.JobFinished(promptId, success, filename, error);
                Log.Info($"[job] reconciled {Short(promptId)} from history: {(success ? "completed" : "failed")}");
            }
        }
    }

    /// <summary>Trims the database past its retention window. Cheap, and only ever once a day.</summary>
    private async Task SweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Store.Sweep();
                Log.Info($"[cfg] local database {Store.SizeBytes() / 1024.0 / 1024.0:0.0} MB after sweep");
            }
            catch (Exception ex)
            {
                Log.Warn($"[warn] database sweep failed: {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromHours(24), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task StudioLoopAsync(CancellationToken ct)
    {
        // XMAN Studio is the account system, not a gate on running. Everything
        // here reports and moves on: a node whose owner has no licence still
        // earns on the free tier, and a website outage must not idle the fleet.
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }

        var registration = await _studio.RegisterDeviceAsync(SelfUpdater.CurrentVersion, ct);
        Log.Info(registration.Ok
            ? "[net] registered with XMAN Studio"
            : $"[net] could not register with XMAN Studio: {registration.Message}");

        while (!ct.IsCancellationRequested)
        {
            LicenseState license = await _studio.ValidateAsync(Options.LicenseKey, ct);
            State.SetLicense(license);
            Log.Info(license.Valid
                ? $"[cfg] licence active — {license.Plan ?? "pro"}{(license.ExpiresAt is { } e ? $", ถึง {e:yyyy-MM-dd}" : "")}"
                : $"[cfg] free tier ({license.Message ?? license.Status})");

            // Round-trip to the relay as a plain HTTP probe: the honest latency
            // number for the status bar, without inventing a ping frame.
            State.SetLatency(await MeasureRelayLatencyAsync(ct));

            try { await Task.Delay(TimeSpan.FromMinutes(30), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<int?> MeasureRelayLatencyAsync(CancellationToken ct)
    {
        try
        {
            var http = new UriBuilder(Options.RelayUrl) { Scheme = Options.RelayUrl.StartsWith("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http", Path = "/healthz" };
            http.Port = new Uri(Options.RelayUrl).Port;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var r = await _http.GetAsync(http.Uri, ct);
            return r.IsSuccessStatusCode ? (int)sw.ElapsedMilliseconds : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task UpdateLoopAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromHours(Math.Max(1, Options.UpdateCheckHours));

        // Breathing room after launch so the first check never competes with
        // connecting to the relay and claiming the first job.
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            // XMAN Studio too, not just the release feed: it is the only channel
            // that can say "this build must not keep running".
            var studioView = await _studio.CheckUpdateAsync(SelfUpdater.CurrentVersion, Options.LicenseKey, ct);
            if (studioView?.ForceUpdate == true)
                Log.Warn($"[cfg] XMAN Studio ระบุว่าเวอร์ชันนี้ใช้ต่อไม่ได้ — ต้องอัปเดตเป็น {studioView.Latest}");

            UpdateCheck check = await Updater.CheckAsync(ct);
            switch (check.Outcome)
            {
                case UpdateOutcome.NotInstalled:
                    State.SetUpdateStatus(null);
                    Log.Info("[cfg] " + (check.Detail ?? "not installed — skipping auto-update"));
                    return;

                case UpdateOutcome.GaveUp:
                    State.SetUpdateStatus(check.Detail);
                    Log.Warn("[cfg] " + (check.Detail ?? "update could not be applied"));
                    return;

                case UpdateOutcome.Downloaded:
                    State.SetUpdateStatus($"อัปเดต {check.AvailableVersion} พร้อมติดตั้ง — รอให้งานปัจจุบันเสร็จ");
                    Log.Info($"[cfg] update {check.AvailableVersion} downloaded — waiting for the node to go idle");

                    while (Runtime.IsBusy && !ct.IsCancellationRequested)
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
                        catch (OperationCanceledException) { return; }
                    }
                    if (ct.IsCancellationRequested) return;

                    UpdatePending = true;
                    State.SetUpdateStatus($"กำลังติดตั้ง {check.AvailableVersion}");
                    Log.Info("[cfg] stopping the node to apply the update");
                    UpdateReady?.Invoke();
                    return;

                case UpdateOutcome.UpToDate:
                    State.SetUpdateStatus(null);
                    break;

                case UpdateOutcome.Failed:
                default:
                    State.SetUpdateStatus(check.Detail);
                    break;
            }

            try { await Task.Delay(period, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>An update is staged and the node is idle. The host should shut down and call <see cref="ApplyPendingUpdate"/>.</summary>
    public event Action? UpdateReady;

    /// <summary>Call after <see cref="DisposeAsync"/>, never before: the runtime and socket must be gone first.</summary>
    public void ApplyPendingUpdate()
    {
        if (!UpdatePending) return;
        Updater.ApplyAndRestart();   // does not return when it succeeds
        Log.Warn("[cfg] update could not be applied — continuing on the current build");
    }

    // ------------------------------------------------------------- events

    private void OnJobEvent(ComfyRuntime.JobEvent e)
    {
        switch (e.Status)
        {
            case JobStatus.Queued:
                Jobs.Submitted(e.PromptId, e.NodesTotal, e.Kind);
                Log.Info($"[job] received {(e.Kind == "job" ? "job" : e.Kind + " job")} {Short(e.PromptId)} ({e.NodesTotal} nodes)");
                break;
            case JobStatus.Running:
                Jobs.Started(e.PromptId);
                Log.Info($"[job] rendering {Short(e.PromptId)}");
                break;
            case JobStatus.Completed:
                Jobs.Finished(e.PromptId, true, e.Filename, null);
                Log.Info($"[job] completed {Short(e.PromptId)}{(e.Filename is null ? "" : $" → {e.Filename}")}");
                break;
            case JobStatus.Failed:
                Jobs.Finished(e.PromptId, false, null, e.Error);
                Log.Warn($"[job] failed {Short(e.PromptId)}: {e.Error}");
                break;
        }
        State.SetCurrentJob(Jobs.Current);
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    /// <summary>A background loop must never take the host down with it.</summary>
    private Task Guard(Task work, string name) => work.ContinueWith(t =>
    {
        if (t.IsFaulted && t.Exception?.InnerException is not OperationCanceledException)
            Log.Warn($"[warn] {name} stopped: {t.Exception?.InnerException?.Message ?? t.Exception?.Message}");
    }, TaskScheduler.Default);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _hostCts.CancelAsync();
        await Task.WhenAny(Task.WhenAll(_background), Task.Delay(TimeSpan.FromSeconds(3)));
        await Runtime.DisposeAsync();
        _mock?.Dispose();
        _telemetry.Dispose();
        _http.Dispose();
        _hostCts.Dispose();
        Log.Info("[cfg] node stopped");   // last write before the store closes
        Store.Dispose();
    }
}
