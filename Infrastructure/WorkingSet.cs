using System.Runtime.InteropServices;

namespace FanControlApp.Infrastructure;

/// <summary>Trims idle memory when hiding to tray or the watchdog waits, so Task Manager doesn't show bloat.</summary>
public static class WorkingSet
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    public static void Trim()
    {
        try
        {
            SetProcessWorkingSetSize(
                System.Diagnostics.Process.GetCurrentProcess().Handle,
                new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // Cosmetic only, never worth failing over.
        }
    }
}
