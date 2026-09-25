using GpuxMine.Core.Net;
using GpuxMine.Core;
using GpuxMine.Core.Licensing;
using GpuxMine.Core.Updates;
using GpuxMine.Node.Assessment;
using GpuxMine.Protocol;

// The host exposes an `Assessment` property, which would otherwise shadow the
// namespace of the same name inside this file.
using AssessmentReport = GpuxMine.Node.Assessment.NodeAssessment;

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
///     dial. The owner's STOP is a drain (<see cref="OwnerStopAsync"/>): new
///     work is refused at once, and the relay socket stays open until what
///     the node already took has been rendered and collected.</item>
/// </list>
/// </remarks>
public sealed partial class NodeHost : IAsyncDisposable
{
    /// <summary>The prototype's telemetry cadence. Fast enough to feel live, slow enough to cost nothing.</summary>
    public static readonly TimeSpan SensePeriod = TimeSpan.FromMilliseconds(1400);

    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long a job the ledger holds open may be missing from ComfyUI —
    /// neither queued nor in its history — before it is written off as failed.
    /// </summary>
    /// <remarks>
    /// Long, because a row from an earlier run cannot be told apart from one
    /// ComfyUI simply has not reached. A prompt this process is still tracking
    /// is written off much sooner — see <see cref="SettleOneAsync"/>.
    /// </remarks>
    private static readonly TimeSpan LostAfter = TimeSpan.FromHours(6);

    /// <summary>How often a drain looks again. It only reads memory, so it can afford to look often.</summary>
    private static readonly TimeSpan DrainPoll = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a stop keeps the relay open after the last customer render
    /// ends. aixman reads the result through the tunnel — <c>/history</c>, then
    /// <c>/view</c> for every file, then <c>/aixman/purge</c> — and it does so on
    /// its own tick, not the instant the render ends.
    /// </summary>
    internal TimeSpan DrainGrace { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The longest a stop waits. Past aixman's own job timeout the job has been
    /// given up on anyway, and a render that never ends must not keep a node
    /// its owner stopped connected for ever.
    /// </summary>
    internal TimeSpan DrainLimit { get; set; } = TimeSpan.FromMinutes(45);

    /// <summary>
    /// The longest a new pairing waits for the old identity's last result to
    /// be collected. A render is never among what it waits for — pairing is
    /// refused while one runs — only a result, and replies on their way.
    /// </summary>
    internal TimeSpan PairHandoverLimit { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The ledger setting that remembers when this build first ran, which bounds the purge fallback.</summary>
    private const string PurgeSinceKey = "purge-fallback-since";

    public NodeOptions Options { get; private set; }
    public NodeSettings Settings { get; }
    public NodeState State { get; } = new();
    public ActivityLog Log { get; }
    public JobHistory Jobs { get; }
    public ComfyRuntime Runtime { get; }
    public SelfUpdater Updater { get; }
    public Storage.NodeStore Store { get; }

    private readonly ITelemetrySource _telemetry;
    private readonly IUserActivitySource _activity;
    private readonly IHostHealthSource _health;
    private readonly XmanStudioClient _studio;
    private readonly HttpClient _http = NodeHttp.Create(TimeSpan.FromSeconds(20));
    private readonly CancellationTokenSource _hostCts = new();
    private readonly List<Task> _background = [];

    /// <summary>Guards the session fields below: START, STOP and a drain can race from the window, the tray and the updater.</summary>
    private readonly Lock _sessionGate = new();
    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;
    private RelayConnection? _connection;

    private CancellationTokenSource? _drainCts;
    private Task? _drainTask;
    private volatile bool _draining;
    private volatile string? _drainReason;

    /// <summary>A pairing code is being exchanged: no new work, because the identity it would belong to is on its way out.</summary>
    private volatile bool _pairing;

    private MockComfy? _mock;
    private DateTimeOffset _lastUserInput = DateTimeOffset.MinValue;

    private readonly SemaphoreSlim _assessGate = new(1, 1);
    private volatile bool _assessing;

    // Asked on every heartbeat and every readiness probe, and the first call
    // costs a process spawn on Windows and a file read on Linux. It cannot
    // change while the process lives, so it is computed once.
    private readonly Lazy<string> _hardwareHash = new(MachineIdentity.HardwareHash, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Set once an update is staged and the node is idle; the host applies it on shutdown.</summary>
    public bool UpdatePending { get; private set; }

    /// <summary>
    /// What this machine was measured able to do. Null until it passes an
    /// assessment, and no work is dispatched to it while it is null.
    /// </summary>
    public AssessmentReport? Assessment { get; private set; }

    public NodeHost(
        NodeOptions options,
        ITelemetrySource? telemetry = null,
        IUserActivitySource? activity = null,
        ILoggerish? alsoLogTo = null,
        IHostHealthSource? health = null)
    {
        Options = options;

        // Before the database is opened: on a node that predates the move, it
        // is still sitting in the folder the installer cleans.
        Storage.DataRescue.FromLegacyLocation(options.DataDirectory, alsoLogTo);

        Store = new Storage.NodeStore(Path.Combine(options.DataDirectory, "node.db"));
        NodeSettings.MigrateLegacyFile(options.DataDirectory, Store);
        Settings = NodeSettings.Load(Store);
        // The sweep is the owner's setting, not a constant compiled into the
        // build. Applied here and again whenever they change it.
        Store.RetentionDays = Settings.HistoryRetentionDays;
        Log = new ActivityLog(Store, alsoLogTo);

        // Said as the first thing in the log, because the owner is about to
        // find their history empty and deserves to know why rather than
        // wonder whether the platform lost their work on purpose.
        if (Store.RecoveredFrom is { } kept)
            Log.Warn($"[warn] ประวัติงานเดิมเสียหาย อ่านไม่ได้ — เริ่มไฟล์ใหม่ ของเดิมเก็บไว้ที่ {kept}");
        Jobs = new JobHistory(Store);

        // The ledger is the second place the identity lives. Done here because
        // this is the first moment both a database and a log exist.
        Options = ReconcileIdentity(Options);

        var nothing = new NullTelemetry();
        _telemetry = telemetry ?? nothing;
        _activity = activity ?? nothing;
        _health = health ?? nothing;

        Runtime = new ComfyRuntime(options, Log, Decide);
        Runtime.Job += OnJobEvent;
        Runtime.AssessmentSource = AssessmentForDispatch;
        Runtime.OffersKind = Offers;
        // The ledger is what lets a purge tell a customer's prompt from the
        // owner's own, including prompts from before the last restart.
        Runtime.Ledger = Store;

        _studio = new XmanStudioClient(_http, options.XmanStudioUrl, log: message => Log.Warn(message));
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
        _background.Add(Guard(Runtime.WatchQueueAsync(_hostCts.Token), "comfyui queue"));
        _background.Add(Guard(AssessLoopAsync(_hostCts.Token), "machine assessment"));
        _background.Add(Guard(SenseLoopAsync(_hostCts.Token), "sensing"));
        _background.Add(Guard(RegisterDeviceLoopAsync(_hostCts.Token), "device registration"));
        _background.Add(Guard(StudioLoopAsync(_hostCts.Token), "xman studio"));
        _background.Add(Guard(PoolStatusLoopAsync(_hostCts.Token), "pool status"));
        _background.Add(Guard(SweepLoopAsync(_hostCts.Token), "database sweep"));
        _background.Add(Guard(ReconcileLoopAsync(_hostCts.Token), "ledger reconcile"));
        _background.Add(Guard(PurgeLoopAsync(_hostCts.Token), "job purge"));
        if (Options.AutoUpdate)
            _background.Add(Guard(UpdateLoopAsync(_hostCts.Token), "updates"));
    }

    /// <summary>
    /// Opens the relay session. Idempotent: pressing it twice does not open two
    /// connections, and pressing it during a stop cancels the stop.
    /// </summary>
    /// <remarks>
    /// Not the owner's START on its own — that is <see cref="OwnerStartAsync"/>,
    /// which also remembers the choice. This is what a launch that resumes
    /// sharing, the headless agent and a finished pairing call.
    /// </remarks>
    public Task StartAsync()
    {
        CancellationTokenSource? abandoned = null;
        lock (_sessionGate)
        {
            if (_draining)
            {
                abandoned = ResumeFromDrainLocked();
            }
            else if (_sessionCts is not null)
            {
                return Task.CompletedTask;
            }
            else if (!Options.Validate(out string error))
            {
                // Checked here and not only by the callers: the ledger may
                // have rescued an identity the window never saw, or lost one
                // the window thought it had, and a relay loop started without
                // one knocks with an empty token for ever.
                Log.Warn($"[net] เริ่มแชร์ไม่ได้ — {error} · ลงทะเบียนเครื่องในหน้า Settings ก่อน");
                return Task.CompletedTask;
            }
            else
            {
                _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_hostCts.Token);
                var connection = new RelayConnection(Options, Runtime, Log, HeartbeatPayload);
                connection.ConnectedChanged += up =>
                    State.SetConnection(up ? ConnectionState.Connected : (State.Running ? ConnectionState.Reconnecting : ConnectionState.Stopped));
                connection.Refused += OnRelayRefused;
                _connection = connection;

                State.SetRunning(true);
                State.SetConnection(ConnectionState.Connecting);
                Log.Info("[net] sharing started");

                _sessionTask = Guard(connection.RunForeverAsync(_sessionCts.Token), "relay session");
            }
        }

        if (abandoned is not null)
        {
            // Outside the lock: cancelling can run the drain's own
            // continuation on this thread, and that is not the place to hold it.
            try { abandoned.Cancel(); } catch (ObjectDisposedException) { /* finished meanwhile */ }
            Log.Info("[net] ยกเลิกการหยุด — กลับมารับงานต่อ");
        }
        Reevaluate();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Closes the relay session now. Whatever is rendering keeps rendering, but
    /// aixman can no longer collect it — the owner's STOP goes through
    /// <see cref="OwnerStopAsync"/>, which waits for delivery first.
    /// </summary>
    public Task StopAsync() => StopSessionAsync(onlyForDrain: null);

    /// <param name="onlyForDrain">
    /// Stop only if this is still the drain in charge. A drain that has just
    /// decided to stop can lose a race with the owner pressing START again, and
    /// must then leave the session they resumed alone.
    /// </param>
    private async Task StopSessionAsync(CancellationTokenSource? onlyForDrain)
    {
        CancellationTokenSource? session, drain;
        Task? running;
        lock (_sessionGate)
        {
            if (onlyForDrain is not null && !ReferenceEquals(_drainCts, onlyForDrain)) return;

            session = _sessionCts;
            if (session is null) return;
            _sessionCts = null;
            running = _sessionTask;
            _sessionTask = null;
            _connection = null;

            drain = _drainCts;
            _drainCts = null;
            _drainTask = null;
            _draining = false;
            _drainReason = null;
        }

        // Stopping from the outside ends a drain that is still waiting.
        if (drain is not null && !ReferenceEquals(drain, onlyForDrain))
        {
            try { await drain.CancelAsync(); } catch (ObjectDisposedException) { /* it finished meanwhile */ }
        }

        Log.Info("[net] sharing stopped");
        await session.CancelAsync();
        if (running is not null)
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(5)));
        session.Dispose();

        State.SetRunning(false);
        State.SetConnection(ConnectionState.Stopped);
    }

    // ------------------------------------------------------------ the switch

    /// <summary>
    /// Whether a launch should start sharing on its own: the machine is paired
    /// and the owner's switch was left on — see <see cref="NodeSettings.SharingEnabled"/>.
    /// </summary>
    public bool ShouldResumeSharing => Options.Validate(out _) && (Settings.SharingEnabled ?? true);

    /// <summary>
    /// Called once at launch, however the program was started — at login, from
    /// the Start menu, by an update, after a pairing.
    /// </summary>
    /// <returns>True when sharing was started.</returns>
    public bool ResumeSharingIfEnabled()
    {
        if (!ShouldResumeSharing) return false;

        if (Settings.SharingEnabled is null)
        {
            // Settle what an earlier build left unsaid, so the next launch
            // reads a decision rather than a default.
            RememberSharing(true);
            Log.Info("[net] เปิดแชร์ต่อจากรุ่นก่อน — กดหยุดเมื่อไรเครื่องจะจำไว้");
        }
        else
        {
            Log.Info("[net] กลับมาแชร์ต่ออัตโนมัติ — ครั้งก่อนเปิดแชร์ค้างไว้");
        }

        _ = StartAsync();
        return true;
    }

    /// <summary>The owner's START: shares now, and on every launch after this until they press STOP.</summary>
    public Task OwnerStartAsync()
    {
        RememberSharing(true);
        // Stopped, the pool status is asked for every fifteen minutes; the
        // owner who just pressed START wants to see the pool take the machine.
        RefreshPoolStatus();
        return StartAsync();
    }

    /// <summary>
    /// The owner's STOP: remembered, so the next launch does not undo it.
    /// </summary>
    /// <param name="now">
    /// Drop the connection at once, forfeiting whatever is in flight. The
    /// default hands it over first — a render that finishes while the node is
    /// offline can never be collected, and the owner is not paid for it.
    /// </param>
    public Task OwnerStopAsync(bool now = false)
    {
        RememberSharing(false);
        return now ? StopAsync() : DrainAsync();
    }

    private void RememberSharing(bool on)
    {
        if (Settings.SharingEnabled == on) return;
        Settings.SharingEnabled = on;
        try
        {
            Settings.Save(Store);
        }
        catch (Exception ex)
        {
            Log.Warn($"[cfg] จำสถานะการแชร์ไม่ได้: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ drain

    /// <summary>
    /// Stops taking work, finishes and hands over what the node already has,
    /// then closes the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// STOP used to close the relay socket on the spot, while its own dialog
    /// told the owner the current job would finish. It did finish — on a
    /// machine aixman could no longer reach, so the result was never
    /// collected, the customer's job was redone elsewhere, and the owner's
    /// history showed DONE for work nobody paid for.
    /// </para>
    /// <para>
    /// While draining, readiness and every new submission answer
    /// <c>503 {stage: "draining"}</c>, and the session stays up until no
    /// customer prompt is open, no request is being answered, and
    /// <see cref="DrainGrace"/> has passed since the last render ended.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes when the session has closed, or when the drain was cancelled by a START.</returns>
    public Task DrainAsync(string? reason = null)
    {
        Task drain;
        string why = reason ?? "กำลังหยุดแชร์ — ทำงานที่รับไว้ให้เสร็จและส่งให้ครบก่อน";
        lock (_sessionGate)
        {
            if (_sessionCts is null) return Task.CompletedTask;
            if (_drainTask is not null) return _drainTask;

            _draining = true;
            _drainReason = why;
            // Before the drain starts, not after: one with nothing to wait for
            // stops the session at once, and a flag raised after that would
            // leave the screen saying "stopping" over a node that has stopped.
            State.SetDraining(true, why);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_hostCts.Token);
            _drainCts = cts;
            // Off this thread: the drain may find nothing to wait for and stop
            // straight away, and that path takes this same lock.
            drain = _drainTask = Task.Run(() => DrainThenStopAsync(cts));
        }

        Log.Info($"[net] หยุดรับงานใหม่ — {why}");
        Reevaluate();
        return drain;
    }

    /// <summary>A drain is waiting for work to be handed over.</summary>
    public bool Draining => _draining;

    /// <returns>The drain being abandoned, for the caller to cancel once it has let go of the lock.</returns>
    private CancellationTokenSource? ResumeFromDrainLocked()
    {
        CancellationTokenSource? drain = _drainCts;
        _drainCts = null;
        _drainTask = null;
        _draining = false;
        _drainReason = null;
        State.SetDraining(false, null);
        return drain;
    }

    private async Task DrainThenStopAsync(CancellationTokenSource cts)
    {
        DateTimeOffset began = DateTimeOffset.UtcNow;
        try
        {
            while (true)
            {
                string? waiting = HandoverPending();
                if (waiting is null) break;

                if (DateTimeOffset.UtcNow - began > DrainLimit)
                {
                    Log.Warn($"[net] รอส่งงานนานเกิน {DrainLimit.TotalMinutes:0} นาที — หยุดแชร์โดยไม่รอต่อ");
                    break;
                }

                lock (_sessionGate)
                {
                    // Only while still in charge: a START in between has
                    // already cleared the flag, and must not see it come back.
                    if (!ReferenceEquals(_drainCts, cts)) return;
                    State.SetDraining(true, $"กำลังหยุดแชร์ — {waiting}");
                }
                await Task.Delay(DrainPoll, cts.Token);
            }

            if (cts.IsCancellationRequested) return;
            await StopSessionAsync(onlyForDrain: cts);
        }
        catch (OperationCanceledException)
        {
            // Resumed by START, stopped outright, or the host is going away.
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>What a drain is still waiting for, in the owner's words, or null when everything has been handed over.</summary>
    private string? HandoverPending()
    {
        if (Runtime.IsWorking)
            return "รอให้งานที่รับไว้เรนเดอร์เสร็จ";

        if ((_connection?.InFlight ?? 0) > 0)
            return "กำลังส่งผลงานให้ pool";

        // aixman purging the job is aixman saying it has the result; only an
        // aixman that predates purge leaves the node to wait out the grace.
        TimeSpan since = DateTimeOffset.UtcNow - Runtime.LastTunnelFinishedAt;
        if (since < DrainGrace && !Runtime.CollectedSinceLastFinish)
            return $"รอ pool เก็บผลงานที่เพิ่งเสร็จ (อีก {(DrainGrace - since).TotalSeconds:0} วินาที)";

        return null;
    }

    /// <summary>
    /// What closing the relay this second would cut off, in the owner's words
    /// — a render, a reply on its way, a result aixman has not collected yet —
    /// or null when nothing would be lost.
    /// </summary>
    /// <remarks>
    /// Wider than <see cref="ComfyRuntime.IsWorking"/>, which only sees the
    /// render. Anything about to close the session outright — quitting, a new
    /// identity — asks this, not that: a render that finished ten seconds ago
    /// is still the customer's until aixman has fetched it.
    /// </remarks>
    public string? UndeliveredWork => HandoverPending();

    private void OnRelayRefused(int status)
    {
        string note = status == 403
            ? "ผู้ดูแลระบบปิดการใช้งานเครื่องนี้ที่ relay — ติดต่อ XMAN Studio หรือจับคู่เครื่องใหม่ในหน้า Settings"
            : "relay ไม่รู้จักเครื่องนี้แล้ว (รหัสเครื่องถูกลบหรือเปลี่ยน) — ลงทะเบียนเครื่องใหม่ในหน้า Settings";
        State.SetRejected(note);
        Log.Warn($"[net] {note}");
    }

    /// <summary>Headless: start, run until asked to stop, stop.</summary>
    /// <param name="ct">Stop now. Whatever is rendering or waiting to be collected is forfeited.</param>
    /// <param name="handOverFirst">
    /// The headless owner's STOP — the agent's first Ctrl+C. The same drain as
    /// the window's STOP: new work is refused at once, and the relay stays open
    /// until what the node already took has been rendered and collected.
    /// </param>
    /// <remarks>
    /// Ctrl+C used to close the relay on the spot, in the middle of a
    /// customer's render. ComfyUI finished it on a machine aixman could no
    /// longer reach: the customer waited out the job timeout, and the owner
    /// was not paid for work their card had done. <paramref name="ct"/> going
    /// off during the handover — a second Ctrl+C — still stops at once.
    /// </remarks>
    public async Task RunAsync(CancellationToken ct, CancellationToken handOverFirst = default)
    {
        Begin();
        await StartAsync();
        using (var either = CancellationTokenSource.CreateLinkedTokenSource(ct, handOverFirst))
        {
            try { await Task.Delay(Timeout.Infinite, either.Token); }
            catch (OperationCanceledException) { /* asked to stop */ }
        }

        if (!ct.IsCancellationRequested)
        {
            try
            {
                await DrainAsync("กำลังหยุดโปรแกรม — ทำงานที่รับไว้ให้เสร็จและส่งให้ครบก่อน").WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Log.Warn("[net] หยุดทันทีตามที่สั่ง — งานที่ยังส่งไม่ครบจะไม่ได้ค่าตอบแทน");
            }
        }
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
    internal AcceptDecision Decide()
    {
        if (!State.Running) return new AcceptDecision(false, "หยุดแชร์อยู่");

        // STOP pressed with work still to hand over. Said as its own stage so
        // aixman can tell "going away" from "back in a minute".
        if (_draining) return new AcceptDecision(false, _drainReason ?? "กำลังหยุดแชร์", ReadyStage.Draining);

        // The identity is being replaced. Work taken now would belong to a
        // worker that is about to disappear — the same going-away as a STOP.
        if (_pairing) return new AcceptDecision(false, "กำลังลงทะเบียนเครื่องใหม่ — ส่งงานที่รับไว้ให้ครบก่อน", ReadyStage.Draining);

        // The benchmark has the card. Taking a job on top of it would both slow
        // the render and measure the machine as slower than it is.
        if (_assessing) return new AcceptDecision(false, "กำลังประเมินเครื่อง");

        // The readiness gate refuses an unassessed node on its own, but it has
        // to be said here too — otherwise the owner's screen reads "ACCEPTING"
        // while the pool is being told 503, and the node looks broken to the
        // one person who could fix it.
        var (report, why) = AssessmentForDispatch();
        if (report is null) return new AcceptDecision(false, why, ReadyStage.Unassessed);

        // The owner turned auto-matching off and ticked only kinds this card
        // cannot do. Accepting here would advertise a machine with nothing on
        // offer.
        if (!report.Capabilities.Any(c => c.CanRun && Offers(c.Kind)))
            return new AcceptDecision(false, "ไม่ได้เลือกรับงานประเภทที่เครื่องนี้ทำได้ — เลือกได้ที่หน้า Models & Jobs");

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

    /// <summary>
    /// Whether the owner offers this kind of work. With auto-matching on, every
    /// kind the card can do; with it off, only the kinds ticked in Models &amp; Jobs.
    /// </summary>
    /// <remarks>
    /// These controls used to change nothing: an owner who unticked video, or
    /// turned auto-matching off, was still listed to the pool for everything
    /// the card could do. The filter is applied to what the heartbeat and
    /// readiness advertise, which is what the dispatcher chooses from.
    /// </remarks>
    private bool Offers(string kind) =>
        Settings.AutoMatch || Settings.AcceptedJobTypes.Contains(kind);

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

    internal AgentTelemetry HeartbeatPayload()
    {
        var g = State.Gpu;
        var (report, _) = AssessmentForDispatch();
        var offered = report?.Capabilities.Where(c => c.CanRun && Offers(c.Kind)).ToArray() ?? [];
        int? queued = Runtime.QueueRemaining;

        return new AgentTelemetry
        {
            // The assessment is the fallback, not an extra: a headless rig has
            // no sensor source wired, and the back office was listing those
            // machines with no card and no VRAM at all. torch told us both
            // during the assessment — there is no reason to show nothing.
            GpuName = g.GpuName ?? report?.GpuName ?? Assessment?.GpuName,
            VramTotalMb = g.VramTotalMb > 0 ? g.VramTotalMb : Assessment?.VramTotalMb ?? 0,
            VramUsedMb = g.VramUsedMb,
            GpuLoadPct = g.LoadPct,
            TempC = g.TempC,
            PowerW = g.PowerW,
            Accepting = State.Accepting,
            // What aixman's database cannot see: a customer render in flight
            // here, or the owner's own batch in ComfyUI's queue. The queue
            // figure is the last one read; the heartbeat cannot wait on a call.
            Busy = Runtime.IsWorking || queued > 0,
            QueueRemaining = queued,
            FreeSharePct = Settings.FreeSharePercent,

            // What the back office lists this machine by. `Assessed` is the
            // dispatchable flag, not "has ever been measured": a stale or failed
            // report reads as false here exactly as it does at the readiness gate.
            Assessed = report is not null,
            Score = report?.Score ?? 0,
            Tier = report?.Tier ?? Assessment?.Tier ?? "unrated",
            // What the owner offers, not only what the card can do: a kind
            // unticked in Models & Jobs is left out of all three lists.
            CanRun = offered.Select(c => c.Kind).ToArray(),
            // Sent alongside, not instead: the pool has to be able to tell a
            // machine that makes an image in twelve seconds from one that makes
            // the same image in four minutes, and CanRun says yes to both.
            Lanes = report is null ? null : offered.ToDictionary(c => c.Kind, c => c.Lane),
            Provisional = report?.Capabilities.Where(c => c.Provisional && Offers(c.Kind)).Select(c => c.Kind).ToArray() ?? [],
            Host = MachineIdentity.MachineName(),
        };
    }

    /// <summary>
    /// Asks XMAN Studio what this machine's owner has earned by inviting people.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in the view model because the studio client and
    /// the node's credentials both belong to the host; the screen should not be
    /// holding a token.
    /// </remarks>
    public Task<Core.Licensing.ReferralSummary?> FetchReferralAsync(CancellationToken ct = default) =>
        _studio.ReferralAsync(Options.WorkerId, Options.Token, ct);

    // ------------------------------------------------------------ identity

    private const string RememberedWorker = "identity-worker";
    private const string RememberedToken = "identity-token";
    private const string RememberedRelay = "identity-relay";

    /// <summary>
    /// Keeps the node's identity in the ledger as well as in <c>agent.json</c>,
    /// and starts from the ledger when the file could not be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A registered machine came up as "ยังไม่ได้ลงทะเบียน" five times on
    /// 2026-09-19 — at 10:43, 10:59, 21:17, 21:18 and 21:49 — while
    /// <c>agent.json</c> sat on disk complete and unchanged since 01:49, and
    /// came up correctly from the same binary at 22:04 and 22:05. A node with
    /// no worker id never opens the relay socket and never asks XMAN Studio for
    /// anything that needs a token, so the owner got a window that looked like
    /// it was running, earned nothing, and showed no referral figures. That is
    /// the whole of "เปิดใหม่แล้วไม่ต่อ xman".
    /// </para>
    /// <para>
    /// Retrying the read handles the moment; this handles the rest. The two
    /// copies fail for different reasons — a file being held by something else
    /// on the machine has nothing to do with SQLite — so a start that cannot
    /// read one can still read the other. When the ledger is the one that
    /// answers, the file is written back from it, so the node repairs itself
    /// instead of depending on this path at every launch.
    /// </para>
    /// <para>
    /// The token sits beside the file it came from, in a folder only this user
    /// can read, and <c>agent.json</c> already holds it in the clear in that
    /// same folder. This adds a copy, not an exposure — and it is the copy that
    /// keeps a machine earning.
    /// </para>
    /// </remarks>
    private NodeOptions ReconcileIdentity(NodeOptions options)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(options.WorkerId) && !string.IsNullOrWhiteSpace(options.Token))
            {
                Remember(options.WorkerId, options.Token, options.RelayUrl);
                return options;
            }

            string? worker = Store.GetSetting(RememberedWorker);
            string? token = Store.GetSetting(RememberedToken);

            // Genuinely unpaired. Nothing to restore, and nothing to say: the
            // Settings screen already asks for a pairing code.
            if (string.IsNullOrWhiteSpace(worker) || string.IsNullOrWhiteSpace(token)) return options;

            string relay = Store.GetSetting(RememberedRelay) is { Length: > 0 } kept ? kept : options.RelayUrl;

            Log.Warn($"[warn] อ่านไฟล์ตัวตนไม่ได้ในรอบนี้ — ใช้ตัวตนที่จำไว้ในฐานข้อมูลแทน: worker {worker}");
            foreach (string note in NodeConfiguration.IdentityNotes)
                Log.Warn($"[warn] {note}");

            // Put the file back, so the next start does not need this path.
            try
            {
                NodeIdentityFile.Save(options.DataDirectory, worker, token, relay);
                Log.Info("[cfg] เขียนไฟล์ตัวตนกลับคืนจากฐานข้อมูลแล้ว");
            }
            catch (Exception ex)
            {
                Log.Warn($"[warn] เขียนไฟล์ตัวตนกลับคืนไม่ได้: {ex.Message}");
            }

            return options with
            {
                WorkerId = worker,
                Token = token,
                RelayUrl = relay,
                IdentityRescuedFrom = Store.Path,
            };
        }
        catch (Exception ex)
        {
            // Never the reason a node fails to start.
            Log.Warn($"[warn] ตรวจตัวตนเครื่องกับฐานข้อมูลไม่สำเร็จ: {ex.Message}");
            return options;
        }
    }

    private void Remember(string workerId, string token, string relayUrl)
    {
        try
        {
            if (Store.GetSetting(RememberedWorker) != workerId) Store.SetSetting(RememberedWorker, workerId);
            if (Store.GetSetting(RememberedToken) != token) Store.SetSetting(RememberedToken, token);
            if (Store.GetSetting(RememberedRelay) != relayUrl) Store.SetSetting(RememberedRelay, relayUrl);
        }
        catch (Exception ex)
        {
            Log.Warn($"[warn] จำตัวตนเครื่องลงฐานข้อมูลไม่ได้: {ex.Message}");
        }
    }

    // ------------------------------------------------------------- pairing

    /// <summary>
    /// Exchanges a pairing code from the website for this machine's identity,
    /// writes it where an update cannot lose it, and starts sharing under it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied in this process. It used to be applied by restarting, and the
    /// restart raced itself: the new copy found the old one still holding the
    /// single-instance lock, decided it was a duplicate and exited, and the
    /// old one then shut down as planned — so a first-time owner watched the
    /// program vanish at the moment pairing succeeded. Only the relay session
    /// is built from the identity, and that is rebuilt here.
    /// </para>
    /// <para>
    /// Refused while a customer's job is on this machine, before the code is
    /// spent: the job belongs to the identity being replaced, and swapping it
    /// out mid-render would leave that job with nobody to deliver it.
    /// </para>
    /// <para>
    /// New work is refused from before that check until the new session is
    /// up (stage <c>draining</c>), because the claim is a network call and the
    /// old session keeps answering aixman while it runs. It used to be taken
    /// the whole time, then cut off mid-render by the switch. Once the claim
    /// has succeeded, a result aixman has not yet collected and replies still
    /// on their way are waited for, not closed on.
    /// </para>
    /// <para>
    /// Pairing is the owner saying "share this machine", so the switch is
    /// turned on and remembered.
    /// </para>
    /// </remarks>
    public async Task<(bool Ok, string Message)> PairAsync(string pairingCode, CancellationToken ct = default)
    {
        string code = pairingCode.Trim();
        if (code.Length < 8)
            return (false, "รหัสจับคู่ต้องมี 8 ตัวอักษร");

        // Raised before the look at the runtime, not after: a submission
        // checks it once more after reserving the card, so nothing can be
        // accepted between this line and the next without the next seeing it.
        _pairing = true;
        bool replaced = false;
        string message;
        try
        {
            if (Runtime.IsWorking)
                return (false, "เครื่องกำลังทำงานของลูกค้าอยู่ — รอให้เสร็จก่อนแล้วค่อยลงทะเบียนใหม่ (รหัสยังไม่ถูกใช้)");

            Reevaluate();
            Log.Info("[net] กำลังลงทะเบียนเครื่องกับ XMAN Studio");

            NodeCredentials credentials = await _studio.ClaimAsync(code, SelfUpdater.CurrentVersion, ct);

            if (!credentials.Ok || credentials.WorkerId is null || credentials.Token is null)
            {
                Log.Warn($"[net] ลงทะเบียนไม่สำเร็จ: {credentials.Message}");
                return (false, credentials.Message ?? "ลงทะเบียนไม่สำเร็จ");
            }

            try
            {
                NodeIdentityFile.Save(Options.DataDirectory, credentials.WorkerId, credentials.Token, credentials.RelayUrl);
            }
            catch (Exception ex)
            {
                // The pairing code has been spent by now, so this is worth saying
                // loudly: the owner has to ask for a new one.
                Log.Warn($"[net] เขียนไฟล์ตั้งค่าไม่ได้: {ex.Message}");
                return (false, $"ลงทะเบียนสำเร็จแต่บันทึกลงเครื่องไม่ได้: {ex.Message} — กรุณาขอรหัสใหม่");
            }

            string relay = string.IsNullOrWhiteSpace(credentials.RelayUrl) ? Options.RelayUrl : credentials.RelayUrl;
            Remember(credentials.WorkerId, credentials.Token, relay);

            Log.Info($"[net] ลงทะเบียนเครื่องสำเร็จ — worker {credentials.WorkerId}"
                     + (credentials.Owner is null ? "" : $" ของ {credentials.Owner}"));

            // A session under the old identity belongs to a worker that is
            // being replaced — and one refused by the relay is exactly what
            // re-pairing is for. What it still owes aixman goes first.
            await HandOverBeforeReplacingAsync(ct);
            await StopAsync();
            Options = Options with
            {
                WorkerId = credentials.WorkerId,
                Token = credentials.Token,
                RelayUrl = relay,
                IdentityRescuedFrom = null,
            };
            // What the website said about the old identity — a refusal, most
            // likely, since that is what sends an owner to re-pair — is not
            // about this one.
            ForgetPool();
            replaced = true;
            message = credentials.Message ?? "ลงทะเบียนเรียบร้อย";
        }
        finally
        {
            _pairing = false;
            if (!replaced) Reevaluate();
        }

        RememberSharing(true);
        await StartAsync();

        return (true, message);
    }

    /// <summary>
    /// Waits, bounded, until the session under the old identity owes aixman
    /// nothing: no reply on its way, no result left uncollected.
    /// </summary>
    /// <remarks>
    /// Only while that session is connected: a node the relay refused, or one
    /// still dialling, cannot be collected from, and waiting would only keep
    /// the owner looking at "กำลังลงทะเบียน…".
    /// </remarks>
    private async Task HandOverBeforeReplacingAsync(CancellationToken ct)
    {
        DateTimeOffset began = DateTimeOffset.UtcNow;
        bool said = false;
        while (true)
        {
            bool sharing;
            lock (_sessionGate) sharing = _sessionCts is not null;
            if (!sharing || State.Connection != ConnectionState.Connected) return;

            string? waiting = HandoverPending();
            if (waiting is null) return;

            if (DateTimeOffset.UtcNow - began > PairHandoverLimit)
            {
                Log.Warn($"[net] เปลี่ยนตัวตนเครื่องโดยไม่รอต่อ — {waiting}");
                return;
            }
            if (!said)
            {
                Log.Info($"[net] รอส่งงานของตัวตนเดิมให้ครบก่อนเปลี่ยน — {waiting}");
                said = true;
            }

            // Cancelled or not, the identity has been claimed and saved: it is
            // applied either way, only sooner.
            try { await Task.Delay(DrainPoll, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ------------------------------------------------------------- assessment

    /// <summary>The report the dispatcher may act on, or why there is none.</summary>
    private (AssessmentReport? Report, string? Reason) AssessmentForDispatch()
    {
        var report = Assessment;

        if (_assessing && report is null) return (null, "กำลังประเมินเครื่องครั้งแรก");
        if (report is null) return (null, "ยังไม่ได้ประเมินเครื่อง");
        if (report.Failed is not null) return (null, $"ประเมินเครื่องไม่ผ่าน: {report.Failed}");

        // The card torch reports now, against the one the report was measured
        // on. Unknown (ComfyUI not answering) leaves the report alone.
        string? gpuNow = Runtime.CurrentGpuHash;
        if (report.GpuChanged(gpuNow))
            return (null, "การ์ดจอไม่ใช่ตัวที่ประเมินไว้ — กำลังประเมินใหม่");
        if (!report.IsUsable(SelfUpdater.CurrentVersion, _hardwareHash.Value, gpuNow))
            return (null, "ผลประเมินหมดอายุหรือฮาร์ดแวร์เปลี่ยน — กำลังประเมินใหม่");

        // Measured, and measured as not able to do anything we dispatch. Saying
        // "ready" here would hand it a job it is certain to fail or to finish so
        // late the customer has given up.
        if (!report.Capabilities.Any(c => c.CanRun))
            return (null, "เครื่องนี้ยังทำงานประเภทใดไม่ได้ — ดูหน้าประเมินเครื่อง");

        return (report, null);
    }

    /// <summary>
    /// Keeps a valid capability report on file, and re-measures when there is not one.
    /// </summary>
    /// <remarks>
    /// Re-measurement is not a nicety: the report goes stale after a month, is
    /// tied to the agent build that took it, and is void if the hardware hash
    /// changes — which is what catches a card being swapped under an enrolled
    /// node, or a report copied onto a slower PC.
    /// </remarks>
    private async Task AssessLoopAsync(CancellationToken ct)
    {
        Assessment = Assessor.Load(Store);
        State.SetAssessment(Assessment);
        if (Assessment is not null) Log.Info($"[gpu] ผลประเมินเดิม: {Assessment.Summary()}");

        // ComfyUI needs a moment after launch, and the first probe asks it to
        // render — there is nothing to gain by racing it.
        try { await Task.Delay(TimeSpan.FromSeconds(8), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            // Which card is in the machine, asked of ComfyUI on every pass, so
            // a swapped card is caught within one pass instead of when the
            // report's month runs out. Cheap: one loopback call, no benchmark.
            try { await Runtime.ReadGpuHashAsync(ct); } catch (OperationCanceledException) { return; }

            var (report, reason) = AssessmentForDispatch();

            // Never benchmark on top of a customer's render: it would steal the
            // card from work somebody is paying for, and mismeasure this machine.
            if (report is null && !Runtime.IsWorking)
            {
                Log.Info($"[gpu] ต้องประเมินเครื่องก่อนรับงาน — {reason}");
                await RunAssessmentAsync(ct);
            }

            // Re-check often while there is no usable report (a node in this
            // state earns nothing). With one, the pass only looks at the card
            // and the report's age; a benchmark runs only when those say so.
            TimeSpan wait = AssessmentForDispatch().Report is null
                ? TimeSpan.FromMinutes(3)
                : TimeSpan.FromMinutes(15);
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Puts a report in place as the assessment loop would on loading one.
    /// For tests, which never start the loops.
    /// </summary>
    internal void UseAssessment(AssessmentReport report)
    {
        Assessment = report;
        State.SetAssessment(report);
        Reevaluate();
    }

    /// <summary>Measures this machine. Safe to call from the UI — only one runs at a time.</summary>
    /// <param name="fromMaximum">
    /// Start the grading over from the top rather than continuing from what the
    /// machine has measured so far. For the owner who has changed the
    /// conditions — raised a power limit, taken ComfyUI off <c>--lowvram</c>,
    /// moved the box off a warm shelf — and would otherwise have to out-run the
    /// times those conditions produced before the lanes would come back up.
    /// </param>
    public async Task<AssessmentReport> RunAssessmentAsync(CancellationToken ct = default, bool fromMaximum = false)
    {
        await _assessGate.WaitAsync(ct);
        try
        {
            _assessing = true;
            State.SetAssessing(true);
            Reevaluate();

            if (fromMaximum)
            {
                // Before the measurement, not after: Score() reads the line
                // while it runs, and the whole point is that it finds nothing
                // on the far side of it.
                try
                {
                    Assessor.ResetGrading(Store);
                    Log.Info("[gpu] เริ่มนับเกรดใหม่ตั้งแต่ตอนนี้ — งานเก่าไม่ถูกลบ แต่ไม่ถูกนับในการจัดเลนอีก");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[gpu] รีเซ็ตการนับเกรดไม่สำเร็จ: {ex.Message}");
                }
            }

            Log.Info("[gpu] กำลังประเมินเครื่อง — วัดความเร็วการ์ดจอด้วยงานมาตรฐาน");

            // Straight into the shared state: the window already redraws on
            // every change it publishes, so the bars follow without a second
            // path into the UI thread.
            var steps = new Progress<AssessmentProgress>(State.SetAssessmentProgress);

            using var assessor = new Assessor(Options, Log, Store, _health, steps);
            AssessmentReport report = await assessor.RunAsync(
                SelfUpdater.CurrentVersion, _hardwareHash.Value, State.Gpu.Driver, ct);

            Assessment = report;
            State.SetAssessment(report);

            try { Assessor.Save(Store, report); }
            catch (Exception ex) { Log.Warn($"[gpu] could not store the assessment: {ex.Message}"); }

            Log.Info($"[gpu] {report.Summary()}");

            // Said before the per-job lines, because a capped card or a host
            // that has been falling over explains most of what follows.
            foreach (string warning in report.Warnings)
                Log.Warn($"[gpu] {warning}");

            foreach (var capability in report.Capabilities.Where(c => c.Lane != "full"))
            {
                Log.Info(capability.Lane == "slow"
                    ? $"[gpu] งาน {capability.Kind}: รับได้แบบไม่เร่ง — {capability.Reason}"
                    : $"[gpu] งาน {capability.Kind}: รับไม่ได้ — {capability.Reason}");
            }

            return report;
        }
        finally
        {
            _assessing = false;
            State.SetAssessing(false);
            Reevaluate();
            _assessGate.Release();
        }
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
            LedgerTotals sinceMidnight = Store.TotalsFor(midnight);
            State.SetCounts(sinceMidnight.Completed, sinceMidnight.Failed);
            State.SetEarnedToday(sinceMidnight.EarnedSatang is { } satang ? satang / 100m : null, sinceMidnight.Unsettled);
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

            try
            {
                await ReconcileOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad pass is not a reason to stop reconciling for the
                // rest of the process's life.
                Log.Warn($"[warn] ledger reconcile failed this pass: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// One pass: every job the ledger holds open, a page at a time, plus every
    /// prompt the runtime is still tracking, held against ComfyUI's queue and
    /// history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to take the twenty oldest open rows and skip any that history
    /// did not have. A row ComfyUI had lost stayed at the front for ever, and
    /// every job behind the twentieth such row was never reconciled.
    /// </para>
    /// <para>
    /// Settling goes through <see cref="JobHistory.Finished"/> and the runtime,
    /// not straight to the database: the Dashboard's current job and the
    /// runtime's busy flag are cleared by the same pass that fixes the row,
    /// instead of showing a phantom render until the next restart.
    /// </para>
    /// </remarks>
    internal async Task ReconcileOnceAsync(CancellationToken ct)
    {
        // The queue first, then what the runtime tracks: a prompt accepted in
        // between is then "tracked but not in the queue" for one pass, which
        // takes two passes to count as lost.
        QueueSnapshot? queue = await Runtime.ReadQueueAsync(ct);
        var tracked = new HashSet<string>(Runtime.TrackedPrompts(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Storage.NodeStore.OpenJob? after = null;
        for (int page = 0; page < 10; page++)
        {
            IReadOnlyList<Storage.NodeStore.OpenJob> rows = Store.UnsettledPage(TimeSpan.FromSeconds(15), limit: 50, after);
            foreach (var row in rows)
            {
                seen.Add(row.PromptId);
                await SettleOneAsync(row.PromptId, row.SubmittedAt, queue, tracked.Contains(row.PromptId), ct);
            }
            if (rows.Count < 50) break;
            after = rows[^1];
        }

        foreach (string promptId in tracked.Where(id => !seen.Contains(id)))
            await SettleOneAsync(promptId, submittedAt: null, queue, tracked: true, ct);
    }

    /// <summary>
    /// Settles one open job from ComfyUI's history, or writes it off when
    /// ComfyUI has neither queued nor finished it.
    /// </summary>
    /// <remarks>
    /// A prompt the runtime is still tracking is written off after two passes
    /// in a row find it nowhere — that is a ComfyUI that died mid-render, and
    /// the flag it left set would otherwise block re-assessment and updates
    /// for good. A row known only to the ledger waits <see cref="LostAfter"/>.
    /// Nothing is written off while ComfyUI cannot be asked: silence is not an
    /// answer.
    /// </remarks>
    private async Task SettleOneAsync(string promptId, DateTimeOffset? submittedAt, QueueSnapshot? queue, bool tracked, CancellationToken ct)
    {
        HistoryEntry entry = await Runtime.HistoryAsync(promptId, ct);
        if (entry.State == HistoryState.Done)
        {
            Finish(promptId, entry.Success, entry.Filename, entry.Error);
            Log.Info($"[job] reconciled {Short(promptId)} from history: {(entry.Success ? "completed" : "failed")}");
            return;
        }

        if (entry.State != HistoryState.Absent || queue is null) return;

        if (queue.PromptIds.Contains(promptId))
        {
            Runtime.NoteFound(promptId);
            return;
        }

        bool lost = tracked
            ? Runtime.NoteMissing(promptId) >= 2
            : submittedAt is { } at && DateTimeOffset.Now - at > LostAfter;
        if (!lost) return;

        Finish(promptId, false, null, "หายจากคิวและประวัติของ ComfyUI — ComfyUI น่าจะถูกปิดหรือรีสตาร์ตระหว่างงาน");
        Log.Warn($"[job] {Short(promptId)} หายจาก ComfyUI — บันทึกเป็นงานล้มเหลว");
    }

    private void Finish(string promptId, bool success, string? filename, string? error)
    {
        Runtime.Settle(promptId, success);
        Jobs.Finished(promptId, success, filename, error);
        State.SetCurrentJob(Jobs.Current);
    }

    /// <summary>
    /// Takes finished customer jobs off this machine when aixman has not asked
    /// to within <see cref="NodeOptions.PurgeAfterHours"/>, and deletes
    /// uploads no prompt ever used.
    /// </summary>
    /// <remarks>
    /// Only jobs that finished after this build first ran: the history an
    /// owner already had is not swept away on the day of the update.
    /// </remarks>
    private async Task PurgeLoopAsync(CancellationToken ct)
    {
        if (Options.PurgeAfterHours <= 0) return;
        TimeSpan after = TimeSpan.FromHours(Options.PurgeAfterHours);
        DateTimeOffset since = PurgeSince();

        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (string promptId in Store.JobsToPurge(DateTimeOffset.UtcNow - after, since))
                {
                    PurgeResult result = await Runtime.PurgeAsync(promptId, ct);
                    // ComfyUI is down: every other row would fail the same way.
                    if (result.Unreachable) break;
                }
                await Runtime.SweepUnclaimedUploadsAsync(after, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"[warn] purge pass failed: {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private DateTimeOffset PurgeSince()
    {
        try
        {
            if (Store.GetSetting(PurgeSinceKey) is { } kept && long.TryParse(kept, out long ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Store.SetSetting(PurgeSinceKey, now.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return DateTimeOffset.FromUnixTimeMilliseconds(now);
        }
        catch
        {
            // Unreadable means "from now": never further back than that.
            return DateTimeOffset.UtcNow;
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

        while (!ct.IsCancellationRequested)
        {
            LicenseState license = await _studio.ValidateAsync(Options.LicenseKey, ct);
            State.SetLicense(license);
            Log.Info(license.Valid
                ? $"[cfg] licence active — {license.Plan ?? "pro"}{(license.ExpiresAt is { } e ? $", ถึง {e.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}" : "")}"
                : $"[cfg] free tier ({license.Message ?? license.Status})");

            // Round-trip to the relay as a plain HTTP probe: the honest latency
            // number for the status bar, without inventing a ping frame.
            State.SetLatency(await MeasureRelayLatencyAsync(ct));

            try { await Task.Delay(TimeSpan.FromMinutes(30), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Registers this machine in XMAN Studio's device list, and keeps trying
    /// until it has.
    /// </summary>
    /// <remarks>
    /// It used to try once per launch. A node started while the website was
    /// down or the network was not up yet — at login, most mornings — was then
    /// missing from the registry until its next restart, which on a machine
    /// that never goes down is never.
    /// </remarks>
    private async Task RegisterDeviceLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }

        TimeSpan wait = TimeSpan.FromMinutes(1);
        for (int attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            var registration = await _studio.RegisterDeviceAsync(SelfUpdater.CurrentVersion, ct);
            if (registration.Ok)
            {
                Log.Info("[net] registered with XMAN Studio");
                return;
            }

            // Said on the first failure and then now and again, not on every
            // retry: an outage is one fact, not thirty lines.
            if (attempt == 1 || attempt % 6 == 0)
                Log.Info($"[net] could not register with XMAN Studio: {registration.Message} — จะลองใหม่อัตโนมัติ");

            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, TimeSpan.FromMinutes(30).Ticks));
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
                    State.SetUpdateStatus($"อัปเดต {check.AvailableVersion} พร้อมติดตั้ง — รอส่งงานปัจจุบันให้เสร็จ");
                    Log.Info($"[cfg] update {check.AvailableVersion} downloaded — handing over work before installing");

                    try
                    {
                        await HandOverForUpdateAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

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

    /// <summary>
    /// Waits until installing an update would cost nobody anything: no
    /// customer render in flight, and the last one collected.
    /// </summary>
    /// <remarks>
    /// A sharing node drains first — it stops taking work so that it actually
    /// becomes idle, where it used to wait for an idle moment that a steadily
    /// busy node might never have. The drain does not touch the owner's switch,
    /// so the new build resumes sharing on its own. An owner who presses START
    /// during it wins; the update waits for a quieter time.
    /// </remarks>
    private async Task HandOverForUpdateAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            bool sharing;
            lock (_sessionGate) sharing = _sessionCts is not null;

            if (sharing)
            {
                await DrainAsync("กำลังจะติดตั้งอัปเดต — ทำงานที่รับไว้ให้เสร็จก่อน แล้วจะกลับมาแชร์เอง").WaitAsync(ct);

                lock (_sessionGate) sharing = _sessionCts is not null;
                if (!sharing) return;

                Log.Info("[cfg] เลื่อนการติดตั้งอัปเดต — เจ้าของกดแชร์ต่อระหว่างรอ จะลองใหม่ในอีก 30 นาที");
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
                continue;
            }

            if (!Runtime.IsWorking) return;
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    /// <summary>An update is staged and the node is idle. The host should shut down and call <see cref="ApplyPendingUpdate"/>.</summary>
    public event Action? UpdateReady;

    /// <summary>Call after <see cref="DisposeAsync"/>, never before: the runtime and socket must be gone first.</summary>
    /// <param name="restartArgs">
    /// What the new build starts with — see <see cref="LaunchFlags.ForRestart"/>.
    /// Whether it shares again is not decided here but by the owner's
    /// remembered switch, the same as any other launch.
    /// </param>
    public void ApplyPendingUpdate(string[]? restartArgs = null)
    {
        if (!UpdatePending) return;
        Updater.ApplyAndRestart(restartArgs);   // does not return when it succeeds
        Log.Warn("[cfg] update could not be applied — continuing on the current build");
    }

    // ------------------------------------------------------------- events

    private void OnJobEvent(ComfyRuntime.JobEvent e)
    {
        switch (e.Status)
        {
            case JobStatus.Queued:
                Jobs.Submitted(e.PromptId, e.NodesTotal, e.Kind);
                if (e.Inputs.Count > 0)
                {
                    // Kept in the ledger, so a purge after a restart still
                    // knows which uploads were this customer's.
                    try { Store.JobInputs(e.PromptId, e.Inputs); }
                    catch (Exception ex) { Log.Warn($"[warn] could not record the job's inputs: {ex.Message}"); }
                }
                Log.Info($"[job] received {(e.Kind == "job" ? "job" : e.Kind + " job")} {Short(e.PromptId)} ({e.NodesTotal} nodes)");
                break;
            // A replayed event was said in the log when it happened; only the
            // record was missing. See ComfyRuntime.JobEvent.Replayed.
            case JobStatus.Running:
                Jobs.Started(e.PromptId);
                if (!e.Replayed) Log.Info($"[job] rendering {Short(e.PromptId)}");
                break;
            case JobStatus.Completed:
                Jobs.Finished(e.PromptId, true, e.Filename, null);
                if (!e.Replayed) Log.Info($"[job] completed {Short(e.PromptId)}{(e.Filename is null ? "" : $" → {e.Filename}")}");
                Retune();
                break;
            case JobStatus.Failed:
                Jobs.Finished(e.PromptId, false, null, e.Error);
                if (!e.Replayed) Log.Warn($"[job] failed {Short(e.PromptId)}: {e.Error}");
                break;
        }
        State.SetCurrentJob(Jobs.Current);
    }

    /// <summary>
    /// Re-grades the stored report against the ledger now that one more job has
    /// been timed, and says so only when a lane actually moved.
    /// </summary>
    /// <remarks>
    /// The other half of starting every node at the top bar. A machine is given
    /// the fast lane on trust, and this is what takes it away — or gives it
    /// back, when the machine turns out to be quicker than the estimate said.
    /// No GPU work, so it is safe to run on the job thread.
    /// </remarks>
    private void Retune()
    {
        if (Assessment is not { } report) return;

        try
        {
            if (!Assessor.Regrade(report, Store)) return;

            Assessor.Save(Store, report);
            State.SetAssessment(report);
            Reevaluate();

            foreach (var capability in report.Capabilities.Where(c => c.Reason is not null && !c.Provisional))
            {
                Log.Info(capability.Lane switch
                {
                    "full" => $"[gpu] งาน {capability.Kind}: เลื่อนขึ้นเต็มความเร็ว — {capability.Reason}",
                    "slow" => $"[gpu] งาน {capability.Kind}: ปรับลงเป็นงานไม่เร่ง — {capability.Reason}",
                    _ => $"[gpu] งาน {capability.Kind}: หยุดรับ — {capability.Reason}",
                });
            }
        }
        catch (Exception ex)
        {
            // Grading is a convenience over a report that is already valid;
            // failing it must never cost the node the job it just finished.
            Log.Warn($"[gpu] could not re-grade after the job: {ex.Message}");
        }
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
        _assessGate.Dispose();
        _poolKick.Dispose();
        _hostCts.Dispose();
        Log.Info("[cfg] node stopped");   // last write before the store closes
        Store.Dispose();
    }
}
