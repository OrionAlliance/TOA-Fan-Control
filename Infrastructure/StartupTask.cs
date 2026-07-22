using System.Diagnostics;

namespace FanControlApp.Infrastructure;

/// <summary>
/// "Start with Windows", done the only way that works for an admin app: a
/// Scheduled Task with highest privileges at logon. (The Startup folder and Run
/// keys silently refuse elevated programs, and would UAC-prompt every boot even
/// if they didn't.) The task launches the app with --minimized, so a boot brings
/// it up silently in the tray instead of opening a window in the user's face.
/// </summary>
public static class StartupTask
{
    private const string TaskName = "TOA - Fan Control";

    /// <summary>Is the logon task registered? (schtasks is the source of truth.)</summary>
    public static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Enable()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;

        // /TR wants the whole command quoted, with the exe path quoted inside it.
        string args =
            $"/Create /F /RL HIGHEST /SC ONLOGON /TN \"{TaskName}\" " +
            $"/TR \"\\\"{exe}\\\" --minimized\"";

        bool ok = Run(args) == 0;
        DebugLog.Write(ok
            ? $"Start-with-Windows enabled (task -> {exe})."
            : "Start-with-Windows enable FAILED (schtasks error).");
        return ok;
    }

    public static bool Disable()
    {
        bool ok = Run($"/Delete /F /TN \"{TaskName}\"") == 0;
        DebugLog.Write(ok
            ? "Start-with-Windows disabled."
            : "Start-with-Windows disable failed (task may not exist).");
        return ok;
    }

    private static int Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process? p = Process.Start(psi);
            if (p == null) return -1;
            p.WaitForExit(10000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex)
        {
            DebugLog.Write("schtasks call failed.", ex);
            return -1;
        }
    }
}
