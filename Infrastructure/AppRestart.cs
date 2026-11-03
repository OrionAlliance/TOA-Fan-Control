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
        _standingBy = RunHelper($"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; " +
                  $"if (-not (Get-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue) -and " +
                  $"-not (Get-Process -Name '{name}' -ErrorAction SilentlyContinue)) {{ {Relaunch()} }}");
        DebugLog.Write("Relaunch helper waiting on the installer, in case it closes the app.");
    }

    private static Process? _standingBy;
    private static bool _sessionEnding;

    /// <summary>Windows (or an installer) is closing the app: the standing-by helper must stay to bring it back.</summary>
    public static void SessionEnding() => _sessionEnding = true;

    /// <summary>The app is closing; if he chose it (not a session end), the standing-by helper must not reopen it.</summary>
    public static void AppClosing()
    {
        if (_sessionEnding || _standingBy == null) return;
        try
        {
            if (!_standingBy.HasExited)
            {
                _standingBy.Kill();
                DebugLog.Write("Closed on purpose during an install - relaunch helper stood down.");
            }
        }
        catch (Exception ex) { DebugLog.Write("Couldn't stand down the relaunch helper.", ex); }
        _standingBy = null;
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

    private static Process? RunHelper(string script)
    {
        if (Environment.ProcessPath == null) return null;
        return Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{script}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
