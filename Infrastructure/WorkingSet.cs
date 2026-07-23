using System.Runtime.InteropServices;

namespace FanControlApp.Infrastructure;

/// <summary>
/// Working-set trim for the two long-idle moments: the app hiding to the tray,
/// and the watchdog settling into its wait. .NET holds heap generously and
/// Windows only reclaims it under memory pressure - correct, but a tray app
/// showing 100+ MB in Task Manager reads as bloat. This tells Windows "reclaim
/// my idle pages now"; anything actually needed pages straight back in.
/// </summary>
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
            // Purely cosmetic - never worth failing anything over.
        }
    }
}
