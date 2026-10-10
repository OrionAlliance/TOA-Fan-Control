using System.Diagnostics;
using System.IO;
using System.Windows;

namespace FanControlApp.Infrastructure;

/// <summary>Removes shortcuts now and app files after exit; fans go back to BIOS, shared PawnIO/.NET stay.</summary>
public static class Uninstaller
{
    public static void Run()
    {
        DebugLog.Write("UNINSTALL requested - removing shortcuts, scheduling folder removal.");

        DeleteShortcuts();
        DeleteInstalledAppsEntry();
        StartupTask.Disable();
        ScheduleFolderRemoval();

        // Normal shutdown so the watchdog restores the BIOS curve before the folder sweep.
        Application.Current.Shutdown();
    }

    /// <summary>Removes Start menu and desktop shortcuts that point at this copy only.</summary>
    private static void DeleteShortcuts()
    {
        string exe = Path.Combine(AppPaths.ExeDir.TrimEnd('\\'), "TOA - Fan Control.exe");

        (string lnk, string where)[] shortcuts =
        {
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "TOA - Fan Control.lnk"), "Start menu"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "TOA - Fan Control.lnk"), "Desktop"),
        };

        foreach ((string lnk, string where) in shortcuts)
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
                    DebugLog.Write($"{where} shortcut removed.");
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Couldn't remove the {where} shortcut.", ex);
            }
        }
    }

    /// <summary>Removes the "Installed apps" entry.</summary>
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
        string exe = Path.GetFileName(Environment.ProcessPath) ?? "TOA - Fan Control.exe";

        // Our own files only, then the folder if empty (a portable copy may sit in Downloads).
        string own = $"del /f /q \"{dir}\\{exe}\" \"{dir}\\fan_debug.log\" \"{dir}\\fan_debug.log.old\" 2>nul" +
                     $" & rd /s /q \"{dir}\\Settings\" 2>nul & rd /s /q \"{dir}\\Updates\" 2>nul & rd \"{dir}\" 2>nul";

        // 5s covers app exit plus the watchdog restoring the fans and exiting.
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/c timeout /t 5 /nobreak >nul & {own}",
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        Process.Start(psi);
        DebugLog.Write("Folder removal scheduled.");
    }
}
