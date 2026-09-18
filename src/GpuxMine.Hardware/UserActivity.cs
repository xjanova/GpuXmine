using System.Runtime.InteropServices;
using GpuxMine.Node;

namespace GpuxMine.Hardware;

/// <summary>
/// "Yield when I use the PC", measured rather than guessed.
/// </summary>
/// <remarks>
/// Two Win32 signals, both cheap and both available without elevation:
/// <list type="bullet">
///   <item><c>GetLastInputInfo</c> — when the keyboard or mouse was last
///     touched. Idle for a couple of minutes means the owner has walked away.</item>
///   <item><c>SHQueryUserNotificationState</c> — Windows' own notion of "do not
///     disturb": a fullscreen Direct3D app (a game) or a presentation. The
///     strongest signal there is that the card is spoken for.</item>
/// </list>
/// </remarks>
public sealed class UserActivity : IUserActivitySource
{
    /// <summary>No input for this long counts as away.</summary>
    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromSeconds(120);

    public bool? IsUserActive()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return null;

            // Both are millisecond tick counts that wrap every 49.7 days; the
            // unsigned subtraction wraps with them.
            uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            return idleMs < IdleThreshold.TotalMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    public bool? IsFullscreenApp()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return null;
            return state is QUNS.RUNNING_D3D_FULL_SCREEN or QUNS.PRESENTATION_MODE or QUNS.BUSY;
        }
        catch
        {
            return null;
        }
    }

    // --- Win32 --------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private enum QUNS
    {
        NOT_PRESENT = 1,
        BUSY = 2,
        RUNNING_D3D_FULL_SCREEN = 3,
        PRESENTATION_MODE = 4,
        ACCEPTS_NOTIFICATIONS = 5,
        QUIET_TIME = 6,
        APP = 7,
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUNS pquns);
}
