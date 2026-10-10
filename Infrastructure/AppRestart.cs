using System.Diagnostics;
using System.IO;

namespace FanControlApp.Infrastructure;

/// <summary>Relaunches the app once this process exits, so the single-instance mutex is free.</summary>
public static class AppRestart
{
    public static void AfterExit()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return;

        // Wait for this process to really exit (not a blind delay), 60s cap; forward args so --minimized sticks.
        string argList = string.Join(",",
            Environment.GetCommandLineArgs().Skip(1).Select(a => $"'{a.Replace("'", "''")}'"));
        string relaunch = argList.Length == 0
            ? $"Start-Process -FilePath '{exe.Replace("'", "''")}'"
            : $"Start-Process -FilePath '{exe.Replace("'", "''")}' -ArgumentList {argList}";

        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = "-NoProfile -WindowStyle Hidden -Command " +
                        $"\"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; {relaunch}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
