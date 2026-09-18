using System.IO;
using Microsoft.Win32;

namespace GpuxMine.App.Shell;

/// <summary>
/// Start Menu shortcut, and launching with Windows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Autostart matters more here, not less, because the server pushes the
/// work.</b> The node dials out and holds a socket open; jobs come down that
/// socket. Nothing can be pushed to a machine where the agent is not running,
/// so an owner who reboots and forgets to reopen the app simply stops earning,
/// silently, until they happen to notice. The pool cannot wake them.
/// </para>
/// <para>
/// The shortcut is created on first successful run rather than by an installer
/// step, so a node that was unzipped rather than installed still ends up
/// somewhere the owner can find it again.
/// </para>
/// </remarks>
public static class DesktopIntegration
{
    private const string AppName = "GPUxMINE";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Creates the Start Menu shortcut once. Cheap to call on every launch.</summary>
    public static void EnsureStartMenuShortcut(Action<string> log)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0 || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            string link = Path.Combine(folder, AppName + ".lnk");

            if (File.Exists(link)) return;

            Directory.CreateDirectory(folder);
            CreateShortcut(link, exe, "แบ่งปันการ์ดจอ รับงาน AI");
            log($"[cfg] created Start Menu shortcut: {link}");
        }
        catch (Exception ex)
        {
            // A missing shortcut is cosmetic. Never let it stop a node starting.
            log($"[warn] could not create shortcut: {ex.Message}");
        }
    }

    public static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string v && v.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutostart(bool enabled, Action<string> log)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) return;
                // Starts to the tray, not to a window in the owner's face every
                // time they log in — the app that does that is the app that gets
                // its autostart switched back off.
                key.SetValue(AppName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }

            log(enabled ? "[cfg] start with Windows: on" : "[cfg] start with Windows: off");
        }
        catch (Exception ex)
        {
            log($"[warn] could not change autostart: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes a .lnk through WScript.Shell.
    /// </summary>
    /// <remarks>
    /// Late-bound COM rather than a reference to the Windows Script Host
    /// interop assembly: one method does not justify an interop dependency
    /// that then has to be published with every build.
    /// </remarks>
    private static void CreateShortcut(string linkPath, string targetPath, string description)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;

        object? shell = Activator.CreateInstance(shellType);
        if (shell is null) return;

        try
        {
            object? shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, [linkPath]);
            if (shortcut is null) return;

            Type sc = shortcut.GetType();
            void Set(string name, object value) =>
                sc.InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, shortcut, [value]);

            Set("TargetPath", targetPath);
            Set("WorkingDirectory", Path.GetDirectoryName(targetPath) ?? "");
            Set("Description", description);
            Set("IconLocation", targetPath + ",0");
            sc.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
