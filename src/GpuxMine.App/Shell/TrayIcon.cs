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
    private readonly Func<Task> _toggle;
    private readonly ToolStripMenuItem _toggleItem;
    private bool _quitting;

    /// <param name="toggle">
    /// The Dashboard dial's own START/STOP, confirmation and all. The tray used
    /// to stop sharing with no question asked and no drain, so the same click
    /// that was careful in the window threw away a finished render here.
    /// </param>
    public TrayIcon(NodeHost host, Window window, Func<Task> toggle)
    {
        _host = host;
        _window = window;
        _toggle = toggle;

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
        _toggleItem.Text = s.Draining ? "กำลังหยุด… (กดเพื่อเลือก)" : s.Running ? "หยุดแชร์" : "เริ่มแชร์";

        string status = !s.Running ? "หยุดอยู่"
            : s.Connection == ConnectionState.Rejected ? "relay ปฏิเสธ — ลงทะเบียนใหม่"
            : s.Draining ? "กำลังหยุด — ส่งงานให้ครบ"
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

    private void ToggleSharing() => _ = _toggle();

    /// <summary>
    /// Quitting while a customer's job is on the machine offers to deliver it
    /// first — the same drain STOP uses — instead of only warning that it will
    /// be lost.
    /// </summary>
    /// <remarks>
    /// Quitting does not touch the remembered switch: the owner closed the
    /// program, they did not decide to stop sharing, and the next launch picks
    /// up where this one left off.
    /// </remarks>
    private async void Quit()
    {
        if (_quitting) return;

        bool working = _host.State.Running && (_host.Runtime.IsWorking || _host.State.Draining);
        if (working)
        {
            var answer = System.Windows.MessageBox.Show(
                "เครื่องกำลังทำงานของลูกค้าอยู่\n\n"
                + "ใช่ — ทำงานนี้ให้เสร็จและส่งให้ลูกค้าก่อน แล้วค่อยปิดโปรแกรมเอง (แนะนำ ได้รับค่าตอบแทนตามปกติ)\n"
                + "ไม่ — ปิดทันที งานนี้จะไม่เสร็จและไม่ได้รับค่าตอบแทน\n"
                + "ยกเลิก — ไม่ปิด",
                "ออกจากโปรแกรม", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return;
            _quitting = true;
            if (answer == MessageBoxResult.Yes)
            {
                _window.Hide();
                _icon.Text = "GPUxMINE — กำลังส่งงานก่อนปิด";
                // Not the owner's STOP: the switch stays as it was, so the
                // next launch shares again if it was sharing.
                await _host.DrainAsync("กำลังปิดโปรแกรม — ทำงานที่รับไว้ให้เสร็จและส่งให้ครบก่อน");

                // The owner pressed START while waiting: they changed their
                // mind about quitting too.
                if (_host.State.Running)
                {
                    _quitting = false;
                    Refresh();
                    return;
                }
            }
        }
        else
        {
            var answer = System.Windows.MessageBox.Show(
                "ออกจากโปรแกรมและหยุดแชร์การ์ดจอ?\n\nเปิดโปรแกรมครั้งหน้าจะกลับมาแชร์ต่อตามที่ตั้งไว้",
                "GPUxMINE", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            _quitting = true;
        }

        _icon.Visible = false;   // before Shutdown, or the icon lingers until hover
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _host.State.Changed -= OnStateChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
