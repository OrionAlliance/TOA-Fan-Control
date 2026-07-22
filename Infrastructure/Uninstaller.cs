using System.Diagnostics;
using System.IO;
using System.Windows;

namespace FanControlApp.Infrastructure;

/// <summary>
/// Removes the app from this PC: shortcuts now, then the install folder (exe,
/// Settings, log) once the process has exited - a running exe can't delete
/// itself, so a detached cmd waits a few seconds and removes the folder after
/// both the app AND its watchdog (a second copy of this exe) are gone. The
/// normal shutdown path hands the fans back to the BIOS on the way out, so
/// uninstalling can never strand them. PawnIO and .NET are left alone: shared
/// system components other software may use.
/// </summary>
public static class Uninstaller
{
    public static void Run()
    {
        DebugLog.Write("UNINSTALL requested - removing shortcuts, scheduling folder removal.");

        DeleteShortcuts();
        DeleteInstalledAppsEntry();
        StartupTask.Disable(); // remove the start-with-Windows task, if registered
        ScheduleFolderRemoval();

        // Normal shutdown: controller disposes, watchdog restores the BIOS curve
        // and exits, then the scheduled removal sweeps the folder.
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Remove our Start-menu and desktop shortcuts - but only ones that actually
    /// point at THIS copy of the app, so uninstalling one install can't strip the
    /// shortcuts of another.
    /// </summary>
    private static void DeleteShortcuts()
    {
        string exe = Path.Combine(AppPaths.ExeDir.TrimEnd('\\'), "TOA - Fan Control.exe");

        string[] shortcuts =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "TOA - Fan Control.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "TOA - Fan Control.lnk"),
        };

        foreach (string lnk in shortcuts)
        {
            try
            {
                if (!File.Exists(lnk)) continue;

                dynamic shell = Activator.CreateInstance(
                    Type.GetTypeFromProgID("WScript.Shell")!)!;
                string target = (string)shell.CreateShortcut(lnk).TargetPath;

                if (string.Equals(target, exe, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(lnk);
                    DebugLog.Write($"Shortcut removed: {lnk}");
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Couldn't remove shortcut '{lnk}'.", ex);
            }
        }
    }

    /// <summary>Remove the entry the installer put in Windows' "Installed apps".</summary>
    private static void DeleteInstalledAppsEntry()
    {
        try
        {
            Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TOA - Fan Control",
                throwOnMissingSubKey: false);
            DebugLog.Write("Installed-apps entry removed.");
        }
        catch (Exception ex)
        {
            DebugLog.Write("Couldn't remove the Installed-apps entry.", ex);
        }
    }

    private static void ScheduleFolderRemoval()
    {
        string dir = AppPaths.ExeDir.TrimEnd('\\');

        // 5 seconds covers the app's release-and-exit plus the watchdog noticing
        // the parent died, restoring the fans, and exiting (it polls at 300ms).
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c timeout /t 5 /nobreak >nul & rd /s /q \"{dir}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        Process.Start(psi);
        DebugLog.Write($"Folder removal scheduled: {dir}");
    }
}
