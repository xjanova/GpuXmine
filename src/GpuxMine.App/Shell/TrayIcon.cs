using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using GpuxMine.Node;
using Application = System.Windows.Application;

namespace GpuxMine.App.Shell;

/// <summary>
/// The notification-area icon: where the node lives while it earns.
/// </summary>
/// <remarks>
/// A worker that runs for days should not need a window on screen, and closing
/// that window must not stop it earning — the owner closed a window, they did
/// not decide to stop sharing their card. Closing hides; quitting is an
/// explicit choice from this menu.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly NodeHost _host;
    private readonly Window _window;
    private readonly ToolStripMenuItem _toggleItem;

    public TrayIcon(NodeHost host, Window window)
    {
        _host = host;
        _window = window;

        var menu = new ContextMenuStrip();
        _toggleItem = new ToolStripMenuItem("เริ่มแชร์", null, (_, _) => ToggleSharing());
        menu.Items.Add(new ToolStripMenuItem("เปิดหน้าต่าง", null, (_, _) => ShowWindow()));
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("ออกจากโปรแกรม", null, (_, _) => Quit()));

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "GPUxMINE",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowWindow();

        host.State.Changed += OnStateChanged;
        Refresh();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/gpuxmine.ico"))?.Stream;
            if (stream is not null) return new Icon(stream);
        }
        catch
        {
            // Fall through to the stock icon rather than failing to start.
        }
        return SystemIcons.Application;
    }

    private void OnStateChanged()
    {
        // The node raises this from background threads; NotifyIcon is a Windows
        // Forms control and wants the UI thread.
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(Refresh);
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        var s = _host.State;
        _toggleItem.Text = s.Running ? "หยุดแชร์" : "เริ่มแชร์";

        string status = !s.Running ? "หยุดอยู่"
            : s.Accepting ? "กำลังแชร์"
            : s.PauseReason ?? "หยุดรับงานชั่วคราว";

        string gpu = s.Gpu.Measured ? $"\n{s.Gpu.GpuName} · {s.Gpu.TempC}°C · {s.Gpu.PowerW} W" : "";

        // Windows truncates the tooltip at 63 characters and silently drops the
        // icon if it is longer, which looks exactly like a crash.
        string text = $"GPUxMINE — {status}{gpu}";
        _icon.Text = text.Length > 62 ? text[..62] : text;
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ToggleSharing()
    {
        if (_host.State.Running) _ = _host.StopAsync();
        else _ = _host.StartAsync();
    }

    private void Quit()
    {
        var answer = System.Windows.MessageBox.Show(
            _host.Runtime.IsBusy
                ? "กำลังเรนเดอร์งานอยู่ ถ้าออกตอนนี้งานปัจจุบันจะไม่เสร็จและไม่ได้รับค่าตอบแทน\n\nออกจากโปรแกรมเลยไหม?"
                : "ออกจากโปรแกรมและหยุดแชร์การ์ดจอ?",
            "GPUxMINE", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            _icon.Visible = false;   // before Shutdown, or the icon lingers until hover
            Application.Current.Shutdown();
        }
    }

    public void Dispose()
    {
        _host.State.Changed -= OnStateChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
