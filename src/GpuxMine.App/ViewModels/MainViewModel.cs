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
using Microsoft.Win32;

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
        _startWithWindows = s.StartWithWindows;
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
    public string ConfigureHint => IsConfigured ? "" : "ยังไม่ได้ตั้ง WorkerId/Token — ดูที่หน้า Settings";

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
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run is null) return;
            if (on)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0) run.SetValue("GPUxMINE", $"\"{exe}\" --minimized");
            }
            else
            {
                run.DeleteValue("GPUxMINE", throwOnMissingValue: false);
            }
            _host.Log.Info(on ? "[cfg] start with Windows: on" : "[cfg] start with Windows: off");
        }
        catch (Exception ex)
        {
            _host.Log.Warn($"[cfg] could not change autostart: {ex.Message}");
        }
    }

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

    public RelayCommand RunBenchmark { get; }
    public string BenchmarkText { get; private set; } = "ยังไม่ได้รัน";
    public string BenchmarkNote => "การทดสอบนี้วัดเวลาที่ ComfyUI ในเครื่องรันกราฟทดสอบ 3 โหนด — ไม่ใช่คะแนน tier ของ pool (M5)";
    public bool BenchmarkRunning { get; private set; }

    private async Task RunBenchmarkAsync()
    {
        if (BenchmarkRunning) return;
        BenchmarkRunning = true; Raise(nameof(BenchmarkRunning));
        BenchmarkText = "กำลังรัน…"; Raise(nameof(BenchmarkText));
        try
        {
            string baseUrl = _host.Options.ComfyUrl.TrimEnd('/');
            const string graph = """
                {"prompt":{"1":{"class_type":"EmptyImage","inputs":{"width":1024,"height":1024,"batch_size":1,"color":8421504}},
                "2":{"class_type":"ImageScale","inputs":{"image":["1",0],"upscale_method":"bicubic","width":2048,"height":2048,"crop":"disabled"}},
                "3":{"class_type":"SaveImage","inputs":{"images":["2",0],"filename_prefix":"gpuxmine_bench"}}},"client_id":"bench"}
                """;
            var sw = Stopwatch.StartNew();
            using var submit = await _http.PostAsync($"{baseUrl}/prompt", new StringContent(graph, Encoding.UTF8, "application/json"));
            string body = await submit.Content.ReadAsStringAsync();
            if (!submit.IsSuccessStatusCode) { BenchmarkText = $"ComfyUI ปฏิเสธ: HTTP {(int)submit.StatusCode}"; return; }
            string? id = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("prompt_id").GetString();

            for (int i = 0; i < 120 && id is not null; i++)
            {
                await Task.Delay(500);
                using var h = await _http.GetAsync($"{baseUrl}/history/{id}");
                string hs = await h.Content.ReadAsStringAsync();
                if (hs.Contains("\"completed\": true") || hs.Contains("\"completed\":true"))
                {
                    BenchmarkText = $"{sw.Elapsed.TotalSeconds:0.00} s สำหรับกราฟทดสอบ (1024² → 2048² bicubic)";
                    _host.Log.Info($"[gpu] benchmark: {sw.Elapsed.TotalSeconds:0.00}s");
                    return;
                }
                if (hs.Contains("\"status_str\": \"error\"")) { BenchmarkText = "กราฟทดสอบล้มเหลวใน ComfyUI"; return; }
            }
            BenchmarkText = "หมดเวลารอ ComfyUI";
        }
        catch (Exception ex)
        {
            BenchmarkText = $"รันไม่ได้: {ex.Message}";
        }
        finally
        {
            BenchmarkRunning = false; Raise(nameof(BenchmarkRunning)); Raise(nameof(BenchmarkText));
        }
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
