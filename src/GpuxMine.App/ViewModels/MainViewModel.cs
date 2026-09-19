using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using GpuxMine.Core;
using GpuxMine.Core.Updates;
using GpuxMine.Node;


namespace GpuxMine.App.ViewModels;

/// <summary>
/// The one view model. Every screen binds to it; it binds to the node.
/// </summary>
/// <remarks>
/// Nothing here is estimated into money. Where the pool has not reported a
/// figure yet, the property is null and the screen shows "—" with a note that
/// says why — never a plausible-looking number. The design mock-up's
/// ฿187.42 was a placeholder, and a placeholder that looks like a balance is
/// how a product earns the word "scam" in every forum.
/// </remarks>
public sealed class MainViewModel : ObservableObject
{
    private readonly NodeHost _host;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _tick;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public MainViewModel(NodeHost host, Dispatcher ui)
    {
        _host = host;
        _ui = ui;

        var s = host.Settings;
        _powerPercent = s.PowerPercent;
        _freeSharePercent = s.FreeSharePercent;
        _yieldWhenActive = s.YieldWhenActive;
        _scheduleOnly = s.ScheduleOnly;
        _autoMatch = s.AutoMatch;
        _tariff = s.TariffPerKwh;
        _offPeakStart = s.OffPeakStartHour;
        _offPeakEnd = s.OffPeakEndHour;
        _offPeakRate = s.OffPeakRatePerKwh;
        _startWithWindows = Shell.DesktopIntegration.IsAutostartEnabled();
        _notifications = s.Notifications;
        _anonymous = s.LeaderboardAnonymous;
        _tempCeiling = s.TempCeilingC;
        _uploadCap = s.UploadCapMbps;
        _acceptImage = s.AcceptedJobTypes.Contains("image");
        _acceptUpscale = s.AcceptedJobTypes.Contains("upscale");
        _acceptText = s.AcceptedJobTypes.Contains("text");
        _acceptVideo = s.AcceptedJobTypes.Contains("video");
        _acceptEmbed = s.AcceptedJobTypes.Contains("embed");
        _currentScreen = s.LastScreen;

        for (int i = 0; i < NodeSettings.HoursPerWeek; i++)
            ScheduleCells.Add(new ScheduleCell(this, i, s.Schedule[i]));

        foreach (var entry in host.Log.Snapshot()) LogEntries.Add(entry);
        host.Log.EntryAdded += e => _ui.BeginInvoke(() =>
        {
            LogEntries.Add(e);
            while (LogEntries.Count > 600) LogEntries.RemoveAt(0);
            Raise(nameof(VisibleLog));
        });

        host.Jobs.Changed += _ => _ui.BeginInvoke(RefreshJobs);
        RefreshJobs();

        ToggleSharing = RelayCommand.Of(() => _ = ToggleSharingAsync());
        SetPreset = new RelayCommand(p => PowerPercent = int.Parse((string)p!));
        ExportLog = RelayCommand.Of(ExportLogToFile);
        RunDiagnostics = RelayCommand.Of(() => _ = RunDiagnosticsAsync());
        RunBenchmark = RelayCommand.Of(() => _ = RunBenchmarkAsync());
        PairNode = RelayCommand.Of(() => _ = PairNodeAsync());
        ChangePairing = RelayCommand.Of(BeginRepair);
        CancelPairingChange = RelayCommand.Of(EndRepair);
        OpenUrl = new RelayCommand(p => OpenInBrowser((string)p!));
        Navigate = new RelayCommand(p => CurrentScreen = (string)p!);
        CopyText = new RelayCommand(p => { try { Clipboard.SetText((string)p!); } catch { /* clipboard busy */ } });

        // Pull, not push: the node's state changes many times a second while
        // rendering, and re-reading it at the prototype's 1.4 s cadence keeps
        // the UI live without flooding the dispatcher.
        _tick = new DispatcherTimer(NodeHost.SensePeriod, DispatcherPriority.Background, (_, _) => Pull(), ui);
        _tick.Start();
        Pull();
    }

    // ------------------------------------------------------------ navigation

    private string _currentScreen;
    public string CurrentScreen
    {
        get => _currentScreen;
        set
        {
            if (Set(ref _currentScreen, value))
            {
                _host.Settings.LastScreen = value;
                SaveSoon();
            }
        }
    }
    public RelayCommand Navigate { get; }

    // ------------------------------------------------------------ live state

    public bool Running { get; private set; }
    public bool Accepting { get; private set; }
    public string? PauseReason { get; private set; }
    public string ConnectionText { get; private set; } = "IDLE · STOPPED";
    public string DialText => Running ? "STOP" : "START";
    public string DialSub => Running ? "sharing GPU" : "idle";
    public bool ShowPauseReason => Running && !Accepting && PauseReason is not null;

    public string GpuName { get; private set; } = "ยังไม่พบการ์ดจอ";
    public string GpuDetail { get; private set; } = "";
    public string DriverText { get; private set; } = "";
    public bool GpuMeasured { get; private set; }
    public int LoadPct { get; private set; }
    public int TempC { get; private set; }
    public int FanPct { get; private set; }
    public int PowerW { get; private set; }
    public double VramUsedGb { get; private set; }
    public double VramTotalGb { get; private set; }
    public double VramFraction => VramTotalGb > 0 ? VramUsedGb / VramTotalGb : 0;
    public string VramText => VramTotalGb > 0 ? $"VRAM {VramUsedGb:0.0} / {VramTotalGb:0.0} GB" : "VRAM —";

    public string JobsTodayText { get; private set; } = "0";
    public string FailedTodayText { get; private set; } = "0";
    public string UptimeText { get; private set; } = "00:00";
    public string EarnedTodayText { get; private set; } = "—";
    public string EarnedNote { get; private set; } = "ยอดจริงจะแสดงเมื่อ pool ส่งรายงานการจ่าย (M2)";
    public string LatencyText { get; private set; } = "—";
    public string LicenseText { get; private set; } = "ระดับฟรี";
    public string? UpdateStatus { get; private set; }
    public string StatusBarLeft { get; private set; } = "IDLE · STOPPED";
    public string StatusBarPower => GpuMeasured ? $"power {PowerPercent}% · {PowerW} W" : $"power {PowerPercent}%";
    public string StatusBarRight { get; private set; } = "";

    public string CurrentJobTitle { get; private set; } = "ไม่มีงาน";
    public string CurrentJobId { get; private set; } = "";
    public int CurrentJobProgress { get; private set; }
    public string CurrentJobProgressText { get; private set; } = "";
    public bool HasCurrentJob { get; private set; }

    public string VersionText => SelfUpdater.CurrentVersion;
    public string WorkerIdText => string.IsNullOrEmpty(_host.Options.WorkerId) ? "ยังไม่ได้ลงทะเบียน" : _host.Options.WorkerId;
    public string RelayHostText => Uri.TryCreate(_host.Options.RelayUrl, UriKind.Absolute, out var u) ? u.Host : "—";
    public string MachineIdShort => MachineIdentity.MachineId()[..12] + "…";
    public bool IsConfigured => _host.Options.Validate(out _);
    public string ConfigureHint => IsConfigured
        ? ""
        : "เครื่องนี้ยังไม่ได้ลงทะเบียน — ขอรหัสจับคู่จากหน้าเครื่องของฉันบนเว็บ แล้วกรอกด้านล่าง";

    private void Pull()
    {
        var st = _host.State;
        var g = st.Gpu;

        Running = st.Running;
        Accepting = st.Accepting;
        PauseReason = st.PauseReason;
        ConnectionText = st.Connection switch
        {
            ConnectionState.Connected => Accepting ? "SHARING · ACTIVE" : "CONNECTED · PAUSED",
            ConnectionState.Connecting => "CONNECTING…",
            ConnectionState.Reconnecting => "RECONNECTING…",
            _ => "IDLE · STOPPED",
        };
        StatusBarLeft = ConnectionText;

        GpuMeasured = g.Measured;
        GpuName = g.GpuName ?? (g.Measured ? "GPU" : "ยังไม่พบการ์ดจอ");
        VramTotalGb = g.VramTotalMb / 1024.0;
        VramUsedGb = g.VramUsedMb / 1024.0;
        GpuDetail = g.Measured ? $"{VramTotalGb:0} GB VRAM" : "อ่านค่าไม่ได้";
        DriverText = g.Driver is null ? "" : $"driver {g.Driver}";
        LoadPct = g.LoadPct; TempC = g.TempC; FanPct = g.FanPct; PowerW = g.PowerW;

        JobsTodayText = st.JobsCompletedToday.ToString("N0");
        FailedTodayText = st.JobsFailedToday.ToString("N0");
        UptimeText = st.Running ? $"{(int)st.Uptime.TotalHours:00}:{st.Uptime.Minutes:00}" : "00:00";
        EarnedTodayText = st.EarnedTodayThb is { } thb ? $"฿{thb:N2}" : "—";
        LatencyText = st.RelayLatencyMs is { } ms ? $"{ms}ms" : "—";
        LicenseText = st.License is { Valid: true } l ? $"Pro · {l.Plan ?? "active"}" : "ระดับฟรี";
        UpdateStatus = st.UpdateStatus;
        StatusBarRight = $"node {WorkerIdText} · relay {RelayHostText}";

        var job = st.CurrentJob;
        HasCurrentJob = job is not null;
        if (job is not null)
        {
            CurrentJobTitle = job.Kind switch
            {
                "image" => "Image generation",
                "upscale" => "Upscale",
                "video" => "Video generation",
                "audio" => "Audio",
                _ => "Job",
            };
            CurrentJobId = $"job #{Short(job.PromptId)} · {job.NodesTotal} nodes";
            CurrentJobProgress = _host.Runtime.ProgressPercent;
            CurrentJobProgressText = job.Status == JobStatus.Running ? $"{CurrentJobProgress}%" : "queued";
        }
        else
        {
            CurrentJobTitle = Running ? (Accepting ? "รองานจาก pool" : "หยุดรับงานชั่วคราว") : "ไม่มีงาน";
            CurrentJobId = ""; CurrentJobProgress = 0; CurrentJobProgressText = "";
        }

        RefreshAssessment();
        RaiseAssessment();

        // Profit calculator: only the half we can measure. Gross needs the pool.
        KwhPerDay = PowerW > 0 ? PowerW / 1000.0 * 24 : 0;
        PowerCostPerDay = (decimal)KwhPerDay * EffectiveTariff();
        Raise(nameof(PowerCostText)); Raise(nameof(KwhText)); Raise(nameof(NetText)); Raise(nameof(GrossText));

        foreach (var name in new[] {
            nameof(Running), nameof(Accepting), nameof(PauseReason), nameof(ConnectionText), nameof(DialText), nameof(DialSub), nameof(ShowPauseReason),
            nameof(GpuMeasured), nameof(GpuName), nameof(GpuDetail), nameof(DriverText), nameof(LoadPct), nameof(TempC), nameof(FanPct), nameof(PowerW),
            nameof(VramUsedGb), nameof(VramTotalGb), nameof(VramFraction), nameof(VramText),
            nameof(JobsTodayText), nameof(FailedTodayText), nameof(UptimeText), nameof(EarnedTodayText), nameof(LatencyText), nameof(LicenseText), nameof(UpdateStatus),
            nameof(StatusBarLeft), nameof(StatusBarPower), nameof(StatusBarRight),
            nameof(CurrentJobTitle), nameof(CurrentJobId), nameof(CurrentJobProgress), nameof(CurrentJobProgressText), nameof(HasCurrentJob),
            nameof(ProfileName), nameof(WattsEstimateText), nameof(VideoFitsCard),
            nameof(ThroughputText), nameof(SuccessRateText), nameof(SuccessRateFraction), nameof(LatencyFraction), nameof(PaidSessionText) })
            Raise(name);
    }

    private static string Short(string id) => id.Length > 8 ? id[..8].ToUpperInvariant() : id;

    // ------------------------------------------------------------ start/stop

    public RelayCommand ToggleSharing { get; }

    private async Task ToggleSharingAsync()
    {
        if (!IsConfigured)
        {
            CurrentScreen = "settings";
            return;
        }

        if (_host.State.Running)
        {
            // Stopping mid-render forfeits that job's payout; the owner should
            // know before they lose it. Nothing is forfeited when idle.
            if (_host.Runtime.IsBusy)
            {
                var ok = MessageBox.Show(
                    "กำลังเรนเดอร์งานอยู่ ถ้าหยุดตอนนี้งานปัจจุบันจะทำต่อจนเสร็จแต่จะไม่รับงานใหม่\n\nหยุดแชร์เลยไหม?",
                    "หยุดแชร์", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ok != MessageBoxResult.Yes) return;
            }
            await _host.StopAsync();
        }
        else
        {
            await _host.StartAsync();
        }
        Pull();
    }

    // ------------------------------------------------------------ settings (two-way)

    private int _powerPercent;
    public int PowerPercent
    {
        get => _powerPercent;
        set { int v = Math.Clamp(value / 5 * 5, 20, 100); if (Set(ref _powerPercent, v)) { _host.Settings.PowerPercent = v; SaveSoon(); Raise(nameof(ProfileName)); Raise(nameof(WattsEstimateText)); Raise(nameof(StatusBarPower)); } }
    }
    public RelayCommand SetPreset { get; }
    public string ProfileName => PowerPercent <= 45 ? "Eco — quiet" : PowerPercent <= 85 ? "Balanced — recommended" : "Maximum — hot";
    /// <summary>The card's own reading when it has one; the design's 2.2 W/% rule of thumb only when it does not.</summary>
    public string WattsEstimateText => GpuMeasured && PowerW > 0 ? $"{PowerW} W draw (measured)" : $"~{(int)(PowerPercent * 2.2)} W (estimate)";

    private int _freeSharePercent;
    public int FreeSharePercent { get => _freeSharePercent; set { int v = Math.Clamp(value, 0, 100); if (Set(ref _freeSharePercent, v)) { _host.Settings.FreeSharePercent = v; SaveSoon(); } } }

    private bool _yieldWhenActive;
    public bool YieldWhenActive { get => _yieldWhenActive; set { if (Set(ref _yieldWhenActive, value)) { _host.Settings.YieldWhenActive = value; SaveSoon(); } } }

    private bool _scheduleOnly;
    public bool ScheduleOnly { get => _scheduleOnly; set { if (Set(ref _scheduleOnly, value)) { _host.Settings.ScheduleOnly = value; SaveSoon(); } } }

    private bool _autoMatch;
    public bool AutoMatch { get => _autoMatch; set { if (Set(ref _autoMatch, value)) { _host.Settings.AutoMatch = value; SaveSoon(); Raise(nameof(AutoMatchTitle)); Raise(nameof(AutoMatchSub)); } } }
    public string AutoMatchTitle => AutoMatch ? "Auto job matching — ON" : "Auto job matching — OFF";
    public string AutoMatchSub => AutoMatch ? "งานถูกจับคู่กับการ์ดของคุณอัตโนมัติ" : "คุณเลือกชนิดงานเองด้านล่าง";

    private bool _acceptImage, _acceptUpscale, _acceptText, _acceptVideo, _acceptEmbed;
    public bool AcceptImage { get => _acceptImage; set { if (Set(ref _acceptImage, value)) SetJobType("image", value); } }
    public bool AcceptUpscale { get => _acceptUpscale; set { if (Set(ref _acceptUpscale, value)) SetJobType("upscale", value); } }
    public bool AcceptText { get => _acceptText; set { if (Set(ref _acceptText, value)) SetJobType("text", value); } }
    public bool AcceptVideo { get => _acceptVideo; set { if (Set(ref _acceptVideo, value)) SetJobType("video", value); } }
    public bool AcceptEmbed { get => _acceptEmbed; set { if (Set(ref _acceptEmbed, value)) SetJobType("embed", value); } }
    private void SetJobType(string kind, bool on) { if (on) _host.Settings.AcceptedJobTypes.Add(kind); else _host.Settings.AcceptedJobTypes.Remove(kind); SaveSoon(); }
    public bool VideoFitsCard => VramTotalGb >= 16;

    private decimal _tariff, _offPeakRate;
    private int _offPeakStart, _offPeakEnd;
    public decimal Tariff { get => _tariff; set { if (Set(ref _tariff, value)) { _host.Settings.TariffPerKwh = value; SaveSoon(); Pull(); } } }
    public decimal OffPeakRate { get => _offPeakRate; set { if (Set(ref _offPeakRate, value)) { _host.Settings.OffPeakRatePerKwh = value; SaveSoon(); Pull(); } } }
    public int OffPeakStart { get => _offPeakStart; set { if (Set(ref _offPeakStart, Math.Clamp(value, 0, 23))) { _host.Settings.OffPeakStartHour = _offPeakStart; SaveSoon(); RefreshScheduleColors(); } } }
    public int OffPeakEnd { get => _offPeakEnd; set { if (Set(ref _offPeakEnd, Math.Clamp(value, 0, 23))) { _host.Settings.OffPeakEndHour = _offPeakEnd; SaveSoon(); RefreshScheduleColors(); } } }
    private decimal EffectiveTariff() => _host.Settings.IsOffPeak(DateTime.Now) ? OffPeakRate : Tariff;

    private int _tempCeiling, _uploadCap;
    public int TempCeiling { get => _tempCeiling; set { if (Set(ref _tempCeiling, Math.Clamp(value, 60, 95))) { _host.Settings.TempCeilingC = _tempCeiling; SaveSoon(); } } }
    public int UploadCap { get => _uploadCap; set { if (Set(ref _uploadCap, Math.Clamp(value, 1, 10000))) { _host.Settings.UploadCapMbps = _uploadCap; SaveSoon(); } } }

    private bool _startWithWindows, _notifications, _anonymous;
    public bool StartWithWindows { get => _startWithWindows; set { if (Set(ref _startWithWindows, value)) { _host.Settings.StartWithWindows = value; ApplyAutostart(value); SaveSoon(); } } }
    public bool Notifications { get => _notifications; set { if (Set(ref _notifications, value)) { _host.Settings.Notifications = value; SaveSoon(); } } }
    public bool LeaderboardAnonymous { get => _anonymous; set { if (Set(ref _anonymous, value)) { _host.Settings.LeaderboardAnonymous = value; SaveSoon(); } } }

    // Profit calculator — the measurable half
    public double KwhPerDay { get; private set; }
    public decimal PowerCostPerDay { get; private set; }
    public string KwhText => $"{KwhPerDay:0.0} kWh/day";
    public string PowerCostText => GpuMeasured && PowerW > 0 ? $"−฿{PowerCostPerDay:N2}" : "—";
    public string GrossText => EarnedTodayText;
    public string NetText => _host.State.EarnedTodayThb is { } e && GpuMeasured ? $"฿{e - PowerCostPerDay:N2}" : "—";

    private DispatcherTimer? _saveTimer;
    private void SaveSoon()
    {
        // Coalesce: a slider fires dozens of changes a second; one write after
        // the hand comes off it is plenty.
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            _host.SaveSettings();
        }, _ui);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void ApplyAutostart(bool on)
        => Shell.DesktopIntegration.SetAutostart(on, on ? _host.Log.Info : _host.Log.Info);

    // ------------------------------------------------------------ schedule

    public ObservableCollection<ScheduleCell> ScheduleCells { get; } = [];

    public void ToggleCell(ScheduleCell cell)
    {
        cell.Share = !cell.Share;
        _host.Settings.Schedule[cell.Index] = cell.Share;
        SaveSoon();
    }

    public bool IsOffPeakHour(int hour) => _host.Settings.IsOffPeak(new DateTime(2000, 1, 3, hour, 0, 0));

    private void RefreshScheduleColors()
    {
        foreach (var c in ScheduleCells) c.Refresh();
    }

    public sealed class ScheduleCell(MainViewModel owner, int index, bool share) : ObservableObject
    {
        public int Index { get; } = index;
        public int Day => Index / 24;
        public int Hour => Index % 24;
        private bool _share = share;
        public bool Share { get => _share; set { if (Set(ref _share, value)) Raise(nameof(State)); } }
        /// <summary>share | cheap | mine — what the cell is painted as.</summary>
        public string State => !Share ? "mine" : owner.IsOffPeakHour(Hour) ? "cheap" : "share";
        public RelayCommand Toggle => RelayCommand.Of(() => owner.ToggleCell(this));
        public void Refresh() => Raise(nameof(State));
    }

    // ------------------------------------------------------------ jobs

    public ObservableCollection<JobRow> JobRows { get; } = [];

    private void RefreshJobs()
    {
        JobRows.Clear();
        foreach (var j in _host.Jobs.Snapshot().Take(60))
            JobRows.Add(new JobRow(j));
        Raise(nameof(ThroughputText)); Raise(nameof(SuccessRateText)); Raise(nameof(PaidSessionText));
    }

    public string ThroughputText
    {
        get
        {
            var st = _host.State;
            double hours = Math.Max(st.Uptime.TotalHours, 1.0 / 60);
            return st.Running ? $"{st.JobsCompletedToday / hours:0.0} jobs/hr" : "—";
        }
    }
    public string SuccessRateText
    {
        get
        {
            var st = _host.State;
            int total = st.JobsCompletedToday + st.JobsFailedToday;
            return total == 0 ? "—" : $"{100.0 * st.JobsCompletedToday / total:0.0}%";
        }
    }
    public string PaidSessionText => EarnedTodayText;

    /// <summary>0–1 for the Benchmark bars; from the same counters as the queue header.</summary>
    public double SuccessRateFraction
    {
        get
        {
            var st = _host.State;
            int total = st.JobsCompletedToday + st.JobsFailedToday;
            return total == 0 ? 0 : (double)st.JobsCompletedToday / total;
        }
    }

    /// <summary>Latency as "how good", 1 at ≤10 ms falling to 0 at 500 ms — the bar reads better full than empty.</summary>
    public double LatencyFraction => _host.State.RelayLatencyMs is { } ms ? Math.Clamp(1 - (ms - 10) / 490.0, 0, 1) : 0;

    public sealed class JobRow(JobRecord j)
    {
        public string Id => "#" + (j.PromptId.Length > 8 ? j.PromptId[..8].ToUpperInvariant() : j.PromptId);
        public string Task => j.Kind switch { "image" => "Image gen", "upscale" => "Upscale", "video" => "Video", "audio" => "Audio", _ => "Job" };
        public string Model => $"{j.NodesTotal} nodes";
        public string Status => j.Status switch { JobStatus.Running => "RUNNING", JobStatus.Completed => "DONE", JobStatus.Failed => "FAILED", _ => "queued" };
        public string When => j.CompletedAt?.ToString("HH:mm:ss") ?? j.StartedAt?.ToString("HH:mm:ss") ?? j.SubmittedAt.ToString("HH:mm:ss");
        public string Duration => j.Duration is { } d ? $"{d.TotalSeconds:0.0}s" : "";
        public string Payout => j.PayoutThb is { } p ? $"+฿{p:N2}" : "—";
        public bool IsRunning => j.Status == JobStatus.Running;
        public bool IsFailed => j.Status == JobStatus.Failed;
        public string? Error => j.Error;
        public string Output => j.OutputFilename ?? "";
    }

    // ------------------------------------------------------------ log

    public ObservableCollection<LogEntry> LogEntries { get; } = [];
    private string _logFilter = "all";
    public string LogFilter { get => _logFilter; set { if (Set(ref _logFilter, value)) Raise(nameof(VisibleLog)); } }
    public IEnumerable<LogEntry> VisibleLog => LogFilter switch
    {
        "jobs" => LogEntries.Where(e => e.Channel is "job" or "auto" or "comfy"),
        "payouts" => LogEntries.Where(e => e.Channel is "pay"),
        "warnings" => LogEntries.Where(e => e.Level == LogLevel.Warn),
        _ => LogEntries,
    };
    public RelayCommand ExportLog { get; }
    private void ExportLogToFile()
    {
        try
        {
            string path = Path.Combine(_host.Options.DataDirectory, $"gpuxmine-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, _host.Log.Export(), Encoding.UTF8);
            _host.Log.Info($"[cfg] log exported to {path}");
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Log.Warn($"[cfg] export failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ diagnostics / benchmark

    public RelayCommand RunDiagnostics { get; }
    public string DiagnosticsText { get; private set; } = "";
    private async Task RunDiagnosticsAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"version      {SelfUpdater.CurrentVersion}");
        sb.AppendLine($"worker       {WorkerIdText}");
        sb.AppendLine($"machine_id   {MachineIdentity.MachineId()}  ({MachineIdentity.FactCount} facts)");
        sb.AppendLine(await ProbeAsync("relay", RelayHealthUrl()));
        sb.AppendLine(await ProbeAsync("comfyui", _host.Options.ComfyUrl.TrimEnd('/') + "/system_stats"));
        sb.AppendLine(await ProbeAsync("xman studio", _host.Options.XmanStudioUrl.TrimEnd('/') + "/api/v1/products/gpuxmine/version"));
        var g = _host.State.Gpu;
        sb.AppendLine(g.Measured ? $"gpu          {g.GpuName} · {g.TempC}°C · {g.PowerW} W · VRAM {g.VramUsedMb}/{g.VramTotalMb} MB" : "gpu          not measured");
        DiagnosticsText = sb.ToString().TrimEnd();
        Raise(nameof(DiagnosticsText));
        _host.Log.Info("[cfg] diagnostics run");
    }

    private string RelayHealthUrl()
    {
        var u = new Uri(_host.Options.RelayUrl);
        string scheme = u.Scheme == "wss" ? "https" : "http";
        return $"{scheme}://{u.Host}:{u.Port}/healthz";
    }

    private async Task<string> ProbeAsync(string name, string url)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var r = await _http.GetAsync(url);
            return $"{name,-12} HTTP {(int)r.StatusCode} in {sw.ElapsedMilliseconds} ms";
        }
        catch (Exception ex)
        {
            return $"{name,-12} FAIL — {ex.Message}";
        }
    }

    // ------------------------------------------------------------ pairing

    /// <summary>
    /// The screen that turns a fresh install into a node that can earn.
    /// </summary>
    /// <remarks>
    /// Until this existed, Settings showed "ยังไม่ได้ลงทะเบียน" and offered
    /// nothing to do about it: worker credentials could only be minted by
    /// someone holding the relay's admin key. Anyone who downloaded the
    /// installer reached a dead end on the first screen.
    /// </remarks>
    public RelayCommand PairNode { get; }

    private string _pairingCode = "";
    public string PairingCode
    {
        get => _pairingCode;
        set => Set(ref _pairingCode, value);
    }

    public bool Pairing { get; private set; }
    public string? PairingStatus { get; private set; }

    /// <summary>Where the owner gets the code — linked, because nobody should have to hunt for it.</summary>
    public string NodesUrl => StudioUrl + "/gpuxmine";

    /// <summary>This machine has an identity: it is registered and the pool knows it.</summary>
    public bool IsPaired => !string.IsNullOrWhiteSpace(_host.Options.WorkerId);

    /// <summary>
    /// The pairing form is shown to a machine that needs it, not to one that
    /// has already been registered.
    /// </summary>
    /// <remarks>
    /// Settings used to show "ลงทะเบียนเครื่องกับ XMAN STUDIO", the
    /// instructions for fetching a code and an empty code box, in exactly the
    /// same way whether the machine had been registered or not. An owner whose
    /// node had been earning for a week still opened Settings and was asked to
    /// register it — the one screen that should have told them the opposite.
    /// </remarks>
    public bool ShowPairingForm => !IsPaired || _repairing;

    /// <summary>What a registered machine is told instead of the form.</summary>
    public string PairedSummary => IsPaired
        ? $"เครื่องนี้ลงทะเบียนกับ XMAN Studio แล้ว · รหัสเครื่อง {_host.Options.WorkerId}"
        : "";

    private bool _repairing;

    /// <summary>
    /// Registering again, over the top of an existing identity.
    /// </summary>
    /// <remarks>
    /// A real need — moving a machine to another account, or re-pairing after
    /// the owner removed it from the website — and a rare one. It is offered
    /// behind a button rather than left open, so the ordinary case is a screen
    /// that says the machine is registered and asks nothing of anybody.
    /// </remarks>
    public RelayCommand ChangePairing { get; }

    /// <summary>
    /// Backs out of re-registering, for the owner who opened it to look.
    /// </summary>
    /// <remarks>
    /// Without this the form had no way back: one click and a machine that was
    /// registered showed the "enter a pairing code" box until the program was
    /// restarted, which reads exactly like a client that cannot make up its
    /// mind about whether it is registered.
    /// </remarks>
    public RelayCommand CancelPairingChange { get; }

    private void BeginRepair()
    {
        _repairing = true;
        PairingStatus = null;
        Raise(nameof(ShowPairingForm)); Raise(nameof(PairingStatus));
    }

    private void EndRepair()
    {
        _repairing = false;
        PairingCode = "";
        PairingStatus = null;
        Raise(nameof(ShowPairingForm)); Raise(nameof(PairingStatus));
    }

    private async Task PairNodeAsync()
    {
        if (Pairing) return;

        Pairing = true;
        PairingStatus = "กำลังลงทะเบียน…";
        Raise(nameof(Pairing)); Raise(nameof(PairingStatus));

        try
        {
            var (ok, message) = await _host.PairAsync(PairingCode);
            PairingStatus = message;

            if (ok)
            {
                PairingCode = "";
                PairingStatus = message + " — กำลังเริ่มโปรแกรมใหม่เพื่อใช้ค่าที่ลงทะเบียน";
                Raise(nameof(PairingStatus));

                // The identity is read at startup, and the relay connection is
                // built from it. Restarting is both the simplest way to apply it
                // and the only one that cannot leave a half-configured node
                // holding a job.
                await Task.Delay(1500);
                Restart();
            }
        }
        catch (Exception ex)
        {
            PairingStatus = $"ลงทะเบียนไม่สำเร็จ: {ex.Message}";
        }
        finally
        {
            Pairing = false;
            Raise(nameof(Pairing)); Raise(nameof(PairingStatus)); Raise(nameof(IsConfigured)); Raise(nameof(ConfigureHint));
            Raise(nameof(IsPaired)); Raise(nameof(ShowPairingForm)); Raise(nameof(PairedSummary)); Raise(nameof(WorkerIdText));
        }
    }

    private static void Restart()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch
        {
            // Could not relaunch — the owner opens it again themselves, and the
            // identity file is already written, so it comes back paired either way.
        }

        Application.Current.Shutdown();
    }

    // ---------------------------------------------------- machine assessment

    /// <summary>
    /// The Benchmark screen is the capability assessment, not a toy timer.
    /// </summary>
    /// <remarks>
    /// It used to run a 3-node graph and print a number that meant nothing to
    /// anyone: the pool never saw it, and the screen said so in its own caption.
    /// The node now has to pass a real assessment before it may receive any
    /// work, so this is where the owner sees the verdict — and, when a kind of
    /// job is refused, exactly why.
    /// </remarks>
    public RelayCommand RunBenchmark { get; }

    public bool BenchmarkRunning { get; private set; }
    public int AssessmentScore { get; private set; }
    public string AssessmentScoreText { get; private set; } = "—";
    public string AssessmentTier { get; private set; } = "ยังไม่ประเมิน";
    public string BenchmarkText { get; private set; } = "ยังไม่ได้ประเมินเครื่อง";
    public string BenchmarkNote { get; private set; } =
        "เครื่องต้องผ่านการประเมินก่อนจึงจะได้รับงาน — ระบบประเมินให้เองเมื่อ ComfyUI พร้อม และประเมินใหม่เมื่อเปลี่ยนการ์ดจอ เปลี่ยนเวอร์ชัน หรือผลเกิน 30 วัน";

    /// <summary>Gauge scale: a reference card sits at 1000, so the dial is read against that.</summary>
    public double AssessmentGaugeValue => Math.Clamp(AssessmentScore / 20.0, 0, 100);

    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];

    public sealed class CapabilityRow(GpuxMine.Node.Assessment.Capability c)
    {
        public string Kind { get; } = c.Kind switch
        {
            "image" => "สร้างภาพ",
            "video" => "สร้างวิดีโอ",
            "upscale" => "ขยายภาพ",
            "audio" => "สร้างเพลง",
            "embed" => "ประมวลผลข้อความ",
            _ => c.Kind,
        };
        public bool CanRun { get; } = c.CanRun;

        /// <summary>"full", "slow" or "no" — the three colours this row is drawn in.</summary>
        public string Lane { get; } = c.Lane;

        /// <summary>The lane was given on trust and has not been earned on real jobs yet.</summary>
        public bool Provisional { get; } = c.Provisional;

        public string Verdict { get; } = c.Lane switch
        {
            // Said out loud, because a lane on trust and a lane earned on
            // thirty finished jobs are not the same promise.
            "full" when c.Provisional => "รับงานได้ · รอบแรก",
            "full" => "รับงานได้",
            // Not a failure and not a full pass. A machine in the slow lane
            // still earns, on work with nobody waiting on the other end.
            "slow" => "งานไม่เร่ง",
            _ => "รับไม่ได้",
        };

        public string Detail { get; } = c.Lane == "full" && !c.Provisional
            ? $"ประมาณ {c.SecondsPerUnit:0.#} วินาที/ชิ้น"
            : c.Reason ?? $"ประมาณ {c.SecondsPerUnit:0.#} วินาที/ชิ้น";
    }

    /// <summary>
    /// What is true about this machine and costs it score, without stopping it
    /// working. Empty on a healthy node, and the panel hides itself.
    /// </summary>
    public ObservableCollection<string> AssessmentWarnings { get; } = [];

    public bool HasAssessmentWarnings => AssessmentWarnings.Count > 0;

    // ------------------------------------------------- assessment in progress

    /// <summary>One row per stage while the assessment runs, each with its own bar.</summary>
    public ObservableCollection<StepRow> AssessmentSteps { get; } = [];

    public sealed class StepRow(GpuxMine.Node.Assessment.AssessmentStep s)
    {
        public string Key { get; } = s.Key;
        public string Label { get; } = s.Label;
        public int Percent { get; } = s.Percent;
        public string Status { get; } = s.Status;

        /// <summary>The number beside the bar. A stage nobody has reached yet shows nothing, not "0%".</summary>
        public string PercentText { get; } = s.Status == "pending" ? "รอคิว" : $"{s.Percent}%";
    }

    public int AssessmentOverallPercent { get; private set; }
    public string AssessmentStageText { get; private set; } = "";

    /// <summary>The panel is only up while a measurement is actually running.</summary>
    public bool ShowAssessmentProgress => BenchmarkRunning && AssessmentSteps.Count > 0;

    private GpuxMine.Node.Assessment.NodeAssessment? _shownAssessment;
    private GpuxMine.Node.Assessment.AssessmentProgress? _shownSteps;

    /// <summary>
    /// Rebuilds the stage rows only when the host has published a new snapshot.
    /// </summary>
    /// <remarks>
    /// This runs on every state change, and a bar that is torn down and rebuilt
    /// on each one flickers all the way through the measurement — the same
    /// reason the capability list is rebuilt by identity rather than on a tick.
    /// </remarks>
    private void RefreshAssessmentSteps()
    {
        var steps = _host.State.AssessmentProgress;
        if (ReferenceEquals(steps, _shownSteps)) return;
        _shownSteps = steps;

        AssessmentOverallPercent = steps.OverallPercent;
        AssessmentStageText = steps.Current;

        AssessmentSteps.Clear();
        foreach (var step in steps.Steps)
            AssessmentSteps.Add(new StepRow(step));

        Raise(nameof(AssessmentOverallPercent));
        Raise(nameof(AssessmentStageText));
        Raise(nameof(ShowAssessmentProgress));
    }

    private void RefreshAssessment()
    {
        var report = _host.State.Assessment;
        BenchmarkRunning = _host.State.Assessing;

        RefreshAssessmentSteps();

        if (BenchmarkRunning && _shownAssessment is null)
        {
            BenchmarkText = "กำลังประเมินเครื่อง…";
            AssessmentTier = "กำลังวัด";
        }

        // Rebuild only when the report itself changed: this runs every 1.4 s,
        // and clearing an ObservableCollection on every tick makes the list
        // flicker for no reason.
        if (ReferenceEquals(report, _shownAssessment)) return;
        _shownAssessment = report;

        Capabilities.Clear();
        AssessmentWarnings.Clear();
        if (report is null)
        {
            AssessmentScore = 0;
            AssessmentScoreText = "—";
            AssessmentTier = "ยังไม่ประเมิน";
            BenchmarkText = "ยังไม่ได้ประเมินเครื่อง";
            return;
        }

        foreach (var capability in report.Capabilities)
            Capabilities.Add(new CapabilityRow(capability));

        foreach (string warning in report.Warnings)
            AssessmentWarnings.Add(warning);
        Raise(nameof(HasAssessmentWarnings));

        if (report.Failed is not null)
        {
            AssessmentScore = 0;
            AssessmentScoreText = "—";
            AssessmentTier = "ประเมินไม่ผ่าน";
            BenchmarkText = report.Failed;
            return;
        }

        AssessmentScore = report.Score;
        AssessmentScoreText = report.Score.ToString("N0");
        AssessmentTier = report.Tier.ToUpperInvariant();
        BenchmarkText =
            $"{report.GpuName ?? "GPU"} · VRAM {report.VramTotalMb / 1024.0:0.#} GB\n" +
            $"งานอ้างอิง {report.ReferenceSeconds:0.00} วินาที (เครื่องอ้างอิง {GpuxMine.Node.Assessment.Assessor.ReferenceBaselineSeconds:0.0} วินาที)\n" +
            // Only when it was actually read: a card that says nothing about
            // its power limits should not get a line claiming it did.
            (report.PowerDefaultW > 0
                ? $"เพดานไฟการ์ด {report.PowerLimitW} W จากสเปค {report.PowerDefaultW} W ({report.PowerPct}%)\n"
                : "") +
            $"โมเดลในเครื่อง: checkpoint {report.Checkpoints.Count} · upscaler {report.Upscalers.Count} · diffusion {report.DiffusionModels.Count}\n" +
            $"วัดเมื่อ {report.MeasuredAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    private async Task RunBenchmarkAsync()
    {
        if (BenchmarkRunning) return;
        try
        {
            await _host.RunAssessmentAsync();
        }
        catch (Exception ex)
        {
            BenchmarkText = $"ประเมินไม่สำเร็จ: {ex.Message}";
        }
        finally
        {
            RefreshAssessment();
            RaiseAssessment();
        }
    }

    private void RaiseAssessment()
    {
        Raise(nameof(BenchmarkRunning)); Raise(nameof(BenchmarkText)); Raise(nameof(BenchmarkNote));
        Raise(nameof(AssessmentScore)); Raise(nameof(AssessmentScoreText));
        Raise(nameof(AssessmentTier)); Raise(nameof(AssessmentGaugeValue));
        Raise(nameof(ShowAssessmentProgress));
    }

    // ------------------------------------------------------------ links

    public RelayCommand OpenUrl { get; }
    public RelayCommand CopyText { get; }
    public string StudioUrl => _host.Options.XmanStudioUrl.TrimEnd('/');
    public string KycUrl => StudioUrl + "/kyc";
    public string WalletUrl => StudioUrl + "/customer/wallet";
    public string ReferralUrl => "https://ai.xman4289.com/referral";

    private static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser registered — nothing sensible to do */ }
    }
}
