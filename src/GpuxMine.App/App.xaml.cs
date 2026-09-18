using System.IO;
using System.Windows;
using GpuxMine.App.ViewModels;
using GpuxMine.Core.Updates;
using GpuxMine.Hardware;
using GpuxMine.Node;
using Microsoft.Extensions.Configuration;

namespace GpuxMine.App;

public partial class App : Application
{
    private NodeHost? _host;
    private MainViewModel? _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("agent.json", optional: true, reloadOnChange: false)
            .AddJsonFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpuxMine", "agent.json"),
                optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("GPUXMINE_")
            .AddCommandLine(e.Args)
            .Build();

        var options = configuration.Get<NodeOptions>() ?? new NodeOptions();

        // Before any window: Velopack's hooks, and our working directory out
        // of the install tree so an update can rename it later.
        SelfUpdater.Prepare(options.DataDirectory);

        // The window opens even when the node is not configured yet: the
        // Settings screen is where the owner fixes that. Only the relay
        // connection needs the worker id and token.
        _host = new NodeHost(options, new GpuTelemetry(), new UserActivity());
        _host.UpdateReady += () => Dispatcher.BeginInvoke(ShutdownForUpdate);

        _vm = new MainViewModel(_host, Dispatcher);
        _host.Begin();

        var window = new MainWindow { DataContext = _vm };
        MainWindow = window;
        window.Show();

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
