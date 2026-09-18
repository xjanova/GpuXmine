using System.IO;
using System.Windows;
using GpuxMine.App.Shell;
using GpuxMine.App.ViewModels;
using GpuxMine.Core.Updates;
using GpuxMine.Hardware;
using GpuxMine.Node;
using Microsoft.Extensions.Configuration;
using Velopack;

namespace GpuxMine.App;

public partial class App : Application
{
    private NodeHost? _host;
    private MainViewModel? _vm;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        // First, before configuration and before any window. Velopack delivers
        // its install, update and uninstall hooks by re-running this executable
        // with special arguments, and this call is what handles them. It has to
        // be in the entry assembly: the packer scans for it here and refuses to
        // build a release if it only finds it in a referenced library.
        VelopackApp.Build().Run();

        base.OnStartup(e);

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("agent.json", optional: true, reloadOnChange: false)
            // The legacy path first, so the one in the current data folder wins
            // if both exist. Neither is required: the Settings screen writes the
            // new one, and the old one is on its way out.
            .AddJsonFile(Path.Combine(NodeOptions.LegacyDataDirectory(), "agent.json"), optional: true, reloadOnChange: false)
            .AddJsonFile(Path.Combine(NodeOptions.DefaultDataDirectory(), "agent.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("GPUXMINE_")
            .AddCommandLine(e.Args)
            .Build();

        var options = configuration.Get<NodeOptions>() ?? new NodeOptions();

        // Our working directory out of the install tree, so an update can
        // rename it later.
        SelfUpdater.Prepare(options.DataDirectory);

        // The window opens even when the node is not configured yet: the
        // Settings screen is where the owner fixes that. Only the relay
        // connection needs the worker id and token.
        _host = new NodeHost(options, new GpuTelemetry(), new UserActivity());
        _host.UpdateReady += () => Dispatcher.BeginInvoke(ShutdownForUpdate);

        _vm = new MainViewModel(_host, Dispatcher);
        _host.Begin();

        // Findable again after the first run, whether the node was installed or
        // just unzipped somewhere.
        DesktopIntegration.EnsureStartMenuShortcut(_host.Log.Info);

        var window = new MainWindow { DataContext = _vm };
        MainWindow = window;

        // The tray is the node's real home; the window is a visit to it.
        // ShutdownMode is OnExplicitShutdown so closing the window — or
        // starting minimised, where no window is ever shown — does not end a
        // process that is in the middle of earning.
        _tray = new TrayIcon(_host, window);

        bool minimised = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
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
        base.OnExit(e);
    }
}
