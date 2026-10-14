using System.Diagnostics;
using System.IO;

namespace FanControlApp.Infrastructure;

/// <summary>Relaunches the app from a small helper outside it, so the single-instance mutex is free.</summary>
public static class AppRestart
{
    /// <summary>Relaunches once this process exits (60s cap, a true hang only).</summary>
    public static void AfterExit() =>
        RunHelper($"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; {Relaunch()}");

    /// <summary>When the given process (an installer) ends, relaunches the app only if it was closed and no copy is running.</summary>
    public static void IfGoneAfter(int pid)
    {
        // Still alive = the installer left it alone, and its own restart takes over (never two copies).
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Replace("'", "''");
        RunHelper($"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; " +
                  $"if (-not (Get-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue) -and " +
                  $"-not (Get-Process -Name '{name}' -ErrorAction SilentlyContinue)) {{ {Relaunch()} }}");
        DebugLog.Write("Relaunch helper waiting on the installer, in case it closes the app.");
    }

    // Forwards the original args so a tray-started app (--minimized) comes back to the tray.
    private static string Relaunch()
    {
        string exe = (Environment.ProcessPath ?? "").Replace("'", "''");
        string argList = string.Join(",",
            Environment.GetCommandLineArgs().Skip(1).Select(a => $"'{a.Replace("'", "''")}'"));
        return argList.Length == 0
            ? $"Start-Process -FilePath '{exe}'"
            : $"Start-Process -FilePath '{exe}' -ArgumentList {argList}";
    }

    private static void RunHelper(string script)
    {
        if (Environment.ProcessPath == null) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{script}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
