using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace FanControlApp.Infrastructure;

/// <summary>
/// "Start with Windows" as a highest-privilege logon Scheduled Task - the only
/// way an admin app can start at boot without a UAC prompt.
/// </summary>
public static class StartupTask
{
    private const string TaskName = "TOA - Fan Control";

    // Only tasks registered by this build carry it - older ones get healed.
    private const string NoTimeLimit = "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>";

    /// <summary>Is the logon task registered? (schtasks is the source of truth.)</summary>
    public static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\"", out _) == 0;

    public static bool Enable()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;

        bool ok = Register(exe);
        DebugLog.Write(ok
            ? "Start-with-Windows enabled."
            : "Start-with-Windows enable FAILED (schtasks error).");
        return ok;
    }

    public static bool Disable()
    {
        bool ok = Run($"/Delete /F /TN \"{TaskName}\"", out _) == 0;
        DebugLog.Write(ok
            ? "Start-with-Windows disabled."
            : "Start-with-Windows disable failed (task may not exist).");
        return ok;
    }

    /// <summary>Re-registers a task left by older builds (3-day kill timer, battery
    /// stops, below-normal priority), keeping the exe it points at.</summary>
    public static void HealIfOutdated()
    {
        try
        {
            if (Run($"/Query /TN \"{TaskName}\" /XML", out string xml) != 0) return; // no task
            if (xml.Contains(NoTimeLimit, StringComparison.OrdinalIgnoreCase)) return;

            // Only the copy the task launches may rewrite it - never repoint it.
            string? exe = Environment.ProcessPath;
            if (exe == null
                || !xml.Contains(SecurityElement.Escape(exe), StringComparison.OrdinalIgnoreCase))
            {
                DebugLog.Write("Start-with-Windows task is outdated but launches another copy - left for that copy to heal.");
                return;
            }

            bool ok = Register(exe);
            DebugLog.Write(ok
                ? "Start-with-Windows task upgraded: no 3-day time limit, runs on battery, normal priority."
                : "Start-with-Windows task upgrade FAILED (schtasks error).");
        }
        catch (Exception ex)
        {
            DebugLog.Write("Start-with-Windows task check failed.", ex);
        }
    }

    private static bool Register(string exe)
    {
        string file = Path.Combine(Path.GetTempPath(), $"toa-fan-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, BuildXml(exe), Encoding.Unicode);
            return Run($"/Create /F /TN \"{TaskName}\" /XML \"{file}\"", out _) == 0;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Writing the startup task definition failed.", ex);
            return false;
        }
        finally
        {
            try { File.Delete(file); } catch { /* a leftover temp file is harmless */ }
        }
    }

    // Overrides Windows' task defaults: a 3-day kill timer, battery stops, and below-normal priority.
    private static string BuildXml(string exe)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        string start = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <Priority>4</Priority>
              </Settings>
              <Triggers>
                <LogonTrigger>
                  <StartBoundary>{start}</StartBoundary>
                </LogonTrigger>
              </Triggers>
              <Actions Context="Author">
                <Exec>
                  <Command>"{SecurityElement.Escape(exe)}"</Command>
                  <Arguments>--minimized</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static int Run(string args, out string output)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process? p = Process.Start(psi);
            if (p == null) return -1;

            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync(); // drained so a full pipe can't stall it
            if (!p.WaitForExit(10000)) return -1;
            output = stdout.Result;
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            DebugLog.Write("schtasks call failed.", ex);
            return -1;
        }
    }
}
