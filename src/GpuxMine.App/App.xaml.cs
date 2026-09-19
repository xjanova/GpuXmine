using GpuxMine.Core.Net;
using System.IO;
using System.Net.Http;
using System.Windows;
using GpuxMine.App.Shell;
using GpuxMine.App.ViewModels;
using GpuxMine.Core.Updates;
using GpuxMine.Hardware;
using GpuxMine.Node;
using Velopack;

namespace GpuxMine.App;

public partial class App : Application
{
    private NodeHost? _host;
    private MainViewModel? _vm;
    private TrayIcon? _tray;
    private NodeInstanceLock? _instanceLock;

    /// <summary>Raises the window of the copy that is already running.</summary>
    /// <remarks>
    /// Best effort by design. Failing to front someone else's window is not a
    /// reason to start a second node, so every failure here still ends in this
    /// copy exiting quietly.
    /// </remarks>
    private static void ShowExistingInstance()
    {
        try
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var other in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                if (other.Id == me.Id) continue;
                if (other.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(other.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(other.MainWindowHandle);
                return;
            }
        }
        catch
        {
            // Nothing to do about it, and nothing worth failing a startup over.
        }
    }

    /// <summary>
    /// Asks the local runtime whether it is there, with a short timeout.
    /// </summary>
    /// <remarks>
    /// A node whose ComfyUI is not running cannot be assessed and cannot earn,
    /// and until now the owner found that out by noticing nothing happened.
    /// One second: this is a loopback call, and a startup screen must never be
    /// the thing that is slow.
    /// </remarks>
    private static string ProbeComfy(string comfyUrl)
    {
        try
        {
            using var http = NodeHttp.Create(TimeSpan.FromSeconds(1));
            using var response = http.GetAsync(comfyUrl.TrimEnd('/') + "/system_stats").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode
                ? $"พร้อม · {comfyUrl}"
                : $"ตอบ HTTP {(int)response.StatusCode} — เปิด ComfyUI ก่อนจึงจะรับงานได้";
        }
        catch
        {
            return "ยังไม่ได้เปิด ComfyUI — เครื่องจะยังรับงานไม่ได้จนกว่าจะเปิด";
        }
    }

    private const int SW_RESTORE = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        // First, before configuration and before any window. Velopack delivers
        // its install, update and uninstall hooks by re-running this executable
        // with special arguments, and this call is what handles them. It has to
        // be in the entry assembly: the packer scans for it here and refuses to
        // build a release if it only finds it in a referenced library.
        //
        // The shortcuts are written here, in the install and update hooks,
        // rather than left to the first ordinary run. Velopack's own shortcuts
        // point into `current`, and `current` is the directory an update
        // renames — so they are wrong the moment the first update lands, and an
        // owner who never opens Settings would never have them corrected. The
        // hook runs while the installer is still on screen, which is the one
        // moment the machine is guaranteed to be in a known state.
        //
        // Both callbacks are the "fast" variants: they run inside the
        // installer's own process and must return quickly. Writing two .lnk
        // files is well inside that, and DesktopIntegration swallows its own
        // failures, so a shortcut that cannot be written can never fail an
        // install.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => DesktopIntegration.EnsureStartMenuShortcut(_ => { }))
            .OnAfterUpdateFastCallback(_ => DesktopIntegration.EnsureStartMenuShortcut(_ => { }))
            .Run();

        base.OnStartup(e);

        var options = NodeConfiguration.Build(e.Args);

        // One node, one process.
        //
        // Nothing stopped a second copy starting, and two copies share
        // everything that matters: the same identity file, the same relay
        // credentials, and the same SQLite ledger. This machine lost that
        // ledger three times in one day to exactly that — two processes with
        // the file open, one of them killed, the WAL left inconsistent. It also
        // made "open the program" a coin toss: whichever window came up was
        // whichever copy the shell happened to activate, and an older one shows
        // older state.
        //
        // The lock is per data directory rather than per machine. Two nodes
        // deliberately run side by side with --DataDirectory, and they have
        // separate ledgers and separate identities; it is sharing one directory
        // that is never intentional. Local\ scope, because the ledger belongs
        // to the signed-in user.
        _instanceLock = NodeInstanceLock.TryAcquire(options.DataDirectory);
        if (_instanceLock is null)
        {
            // Bring the window that is already running forward rather than
            // saying nothing: the owner clicked the icon because they wanted to
            // see the program, and a click that appears to do nothing is how
            // people end up starting a third copy.
            ShowExistingInstance();
            Shutdown();
            return;
        }

        bool minimised = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        // Nothing is shown when the node is starting to the tray at login: a
        // splash that appears over somebody's desktop every morning is not a
        // courtesy.
        SplashWindow? splash = null;
        if (!minimised)
        {
            splash = new SplashWindow();
            splash.Begin(5);
            splash.Show();
        }

        splash?.Step("เตรียมโฟลเดอร์ข้อมูล");
        // Our working directory out of the install tree, so an update can
        // rename it later.
        SelfUpdater.Prepare(options.DataDirectory);
        splash?.Done(options.DataDirectory);

        splash?.Step("เปิดบันทึกงานของเครื่อง");
        // The window opens even when the node is not configured yet: the
        // Settings screen is where the owner fixes that. Only the relay
        // connection needs the worker id and token.
        _host = new NodeHost(options, new GpuTelemetry(), new UserActivity(),
            health: new WindowsHostHealth());
        _host.UpdateReady += () => Dispatcher.BeginInvoke(ShutdownForUpdate);
        splash?.Done(_host.Store.RecoveredFrom is null
            ? $"ปกติ · {_host.Store.SizeBytes() / 1024.0 / 1024.0:0.0} MB"
            : "ไฟล์เดิมเสียหาย เริ่มไฟล์ใหม่ ของเดิมเก็บไว้แล้ว");

        // The check that has been guessed at rather than seen.
        //
        // The Settings screen showed "not registered" on a machine that was,
        // and came back to it after a restart. Nothing recorded what the
        // program had actually resolved, so there was nothing to compare
        // against. Now the identity is read out at startup, on screen and in
        // the log, every single launch.
        splash?.Step("ตรวจการลงทะเบียนเครื่อง");

        // Read from the host, not from `options`: the host reconciles the
        // identity against the ledger as it opens, so a machine whose file was
        // unreadable this once is already paired again by the time we get here
        // and must not be announced as a stranger.
        NodeOptions resolved = _host.Options;
        bool paired = !string.IsNullOrWhiteSpace(resolved.WorkerId);
        string identity = paired
            ? $"{resolved.WorkerId} · relay {resolved.RelayUrl}"
            : "ยังไม่ได้ลงทะเบียน — กรอกรหัสจับคู่ในหน้า Settings";
        if (!paired && NodeConfiguration.LastIdentitySearch is { } looked)
            _host.Log.Warn($"[warn] หาไฟล์ตัวตนไม่เจอในโฟลเดอร์เหล่านี้: {looked}");

        // Every reason it gave, not just the verdict. This is the line that was
        // missing while the machine came up unregistered five times in a day.
        if (!paired)
        {
            foreach (string note in NodeConfiguration.IdentityNotes)
                _host.Log.Warn($"[warn] {note}");
        }

        _host.Log.Info($"[cfg] ตัวตนเครื่องตอนเปิด: {identity}");
        if (resolved.IdentityRescuedFrom is { } rescued)
            _host.Log.Warn($"[warn] ตั้งค่าปกติอ่านตัวตนไม่เจอ — กู้มาจาก {rescued}");
        splash?.Done(identity);

        splash?.Step("เตรียมหน้าจอ");
        _vm = new MainViewModel(_host, Dispatcher);
        _host.Begin();

        // Findable again after the first run, whether the node was installed or
        // just unzipped somewhere.
        DesktopIntegration.EnsureStartMenuShortcut(_host.Log.Info);
        splash?.Done("พร้อม");

        splash?.Step("ตรวจ ComfyUI");
        splash?.Done(ProbeComfy(options.ComfyUrl));

        var window = new MainWindow { DataContext = _vm };
        MainWindow = window;

        // The tray is the node's real home; the window is a visit to it.
        // ShutdownMode is OnExplicitShutdown so closing the window — or
        // starting minimised, where no window is ever shown — does not end a
        // process that is in the middle of earning.
        _tray = new TrayIcon(_host, window);

        // The splash goes before the window appears, not after: two windows on
        // screen at once is worse than no splash at all.
        splash?.Close();
        if (!minimised) window.Show();

        // Autostart implies the owner wants it earning, not just running.
        if (minimised && options.Validate(out _)) _ = _host.StartAsync();

        DispatcherUnhandledException += (_, args) =>
        {
            _host?.Log.Warn($"[warn] unhandled: {args.Exception.Message}");
            args.Handled = true;   // a UI hiccup must not take the node down mid-render
        };
    }

    private async void ShutdownForUpdate()
    {
        if (_host is null) return;
        var host = _host;
        _host = null;
        await host.DisposeAsync();
        host.ApplyPendingUpdate();   // does not return on success
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _tray = null;

        var host = _host;
        _host = null;
        if (host is not null)
        {
            // Synchronous on purpose: OnExit is the last thing that runs, and
            // an un-awaited dispose here would let the process end with the
            // relay socket half-closed.
            //
            // Run on the pool, not inline. DisposeAsync awaits several times,
            // and each continuation wants to resume on the captured context —
            // this thread, which would be blocked in GetResult() waiting for
            // them. The first cut did exactly that; the window closed, the
            // process sat there, and the test harness had to kill it.
            try
            {
                Task.Run(() => host.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                host.Log.Warn($"[warn] shutdown: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
        // Released last, so the name stays taken for as long as this process
        // could still be holding the ledger.
        _instanceLock?.Dispose();

        base.OnExit(e);
    }
}
