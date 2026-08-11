using System.Diagnostics;

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

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c timeout /t 5 /nobreak >nul & start \"\" \"{exe}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
