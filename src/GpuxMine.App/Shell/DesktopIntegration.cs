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

    /// <summary>
    /// The executable a shortcut or autostart entry should point at, or null
    /// when this copy is not an install and has no business owning either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both of these used to point at <c>Environment.ProcessPath</c> — whichever
    /// copy happened to be running. Run the app once out of <c>bin\Debug</c> and
    /// it took the Start Menu entry for itself, and because the old code skipped
    /// whenever the file already existed, nothing ever corrected it: the owner's
    /// shortcut opened a build directory from then on. Autostart was worse — it
    /// would launch that build at every login, from a path that disappears the
    /// next time the tree is cleaned.
    /// </para>
    /// <para>
    /// The install layout is the test, because it is the thing that is actually
    /// true: Velopack puts the running app in <c>current</c> beside its
    /// <c>Update.exe</c>. A build tree and an unzipped copy match neither.
    /// </para>
    /// <para>
    /// The launcher is the stub in the install root, not the executable inside
    /// <c>current</c>. Applying an update renames that directory, so the stub is
    /// the only entry point that keeps working across versions.
    /// </para>
    /// </remarks>
    private static string? InstalledLauncher()
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return null;

        DirectoryInfo? dir = new FileInfo(exe).Directory;
        if (dir is null || !dir.Name.Equals("current", StringComparison.OrdinalIgnoreCase)) return null;

        DirectoryInfo? root = dir.Parent;
        if (root is null || !File.Exists(Path.Combine(root.FullName, "Update.exe"))) return null;

        string stub = Path.Combine(root.FullName, Path.GetFileName(exe));
        return File.Exists(stub) ? stub : exe;
    }

    /// <summary>Where an existing .lnk currently points, or null if it cannot be read.</summary>
    private static string? ShortcutTarget(string linkPath)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return null;

        object? shell = Activator.CreateInstance(shellType);
        if (shell is null) return null;

        try
        {
            object? shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, [linkPath]);
            return shortcut?.GetType().InvokeMember("TargetPath",
                System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
        }
        catch
        {
            return null;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Points the owner's shortcuts at the installed app, creating them if they
    /// are missing and correcting them if they point somewhere else. Cheap to
    /// call on every launch.
    /// </summary>
    /// <remarks>
    /// A copy that is not an install does nothing here at all — it neither
    /// creates a shortcut nor touches one that exists. That is the whole point:
    /// the owner's Start Menu entry should survive a developer, or the owner
    /// themselves, running the program from somewhere else.
    /// </remarks>
    public static void EnsureStartMenuShortcut(Action<string> log)
    {
        try
        {
            if (InstalledLauncher() is not { } launcher)
            {
                // Not a failure, and not silent: an owner looking at the log
                // after clicking a shortcut that opened the wrong thing should
                // find the sentence that explains it.
                log("[cfg] ไม่ได้รันจากตัวที่ติดตั้ง — ไม่แตะช็อตคัตและ autostart");
                return;
            }

            string startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            Directory.CreateDirectory(startMenu);

            Repair(Path.Combine(startMenu, AppName + ".lnk"), launcher, log);

            // The desktop one is the icon people actually double-click, and the
            // installer puts it there. Repaired, never created: an owner who
            // deleted it meant to.
            string desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
            if (File.Exists(desktop)) Repair(desktop, launcher, log);

            // An autostart entry left pointing at a build tree stops working the
            // moment that tree is cleaned, and the node silently stops earning.
            if (IsAutostartEnabled()) SetAutostart(true, log);
        }
        catch (Exception ex)
        {
            // A missing shortcut is cosmetic. Never let it stop a node starting.
            log($"[warn] could not create shortcut: {ex.Message}");
        }
    }

    private static void Repair(string link, string launcher, Action<string> log)
    {
        if (File.Exists(link))
        {
            string? current = ShortcutTarget(link);
            if (string.Equals(current, launcher, StringComparison.OrdinalIgnoreCase)) return;

            CreateShortcut(link, launcher, "แบ่งปันการ์ดจอ รับงาน AI");
            log($"[cfg] ช็อตคัตชี้ผิดที่ แก้แล้ว: {current ?? "?"} -> {launcher}");
            return;
        }

        CreateShortcut(link, launcher, "แบ่งปันการ์ดจอ รับงาน AI");
        log($"[cfg] created shortcut: {link}");
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
                // The stub, so the entry keeps working after an update renames
                // `current`, and never a build tree that may not exist tomorrow.
                string exe = InstalledLauncher() ?? "";
                if (exe.Length == 0)
                {
                    log("[warn] ยังไม่ได้ติดตั้ง — เปิดพร้อม Windows ได้เฉพาะตัวที่ติดตั้งแล้ว");
                    return;
                }
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
