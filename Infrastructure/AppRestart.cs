using System.Diagnostics;
using System.Linq;

namespace FanControlApp.Infrastructure;

/// <summary>
/// Relaunches the app after the current process exits. A detached waiter does
/// the launch - starting immediately would trip the single-instance mutex the
/// dying process still holds.
/// </summary>
public static class AppRestart
{
    public static void AfterExit()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return;

        // The waiter waits for THIS process to actually exit (the mutex frees the
        // moment it dies) rather than a blind delay - a slow shutdown must never
        // lose the relaunch race and leave nothing running. The 60s cap only
        // breaks a true hang. Original args (e.g. --minimized) are forwarded so
        // a tray-launched app returns to the tray.
        string argList = string.Join(",",
            Environment.GetCommandLineArgs().Skip(1).Select(a => $"'{a.Replace("'", "''")}'"));
        string relaunch = argList.Length == 0
            ? $"Start-Process -FilePath '{exe.Replace("'", "''")}'"
            : $"Start-Process -FilePath '{exe.Replace("'", "''")}' -ArgumentList {argList}";

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -WindowStyle Hidden -Command " +
                        $"\"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; {relaunch}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
