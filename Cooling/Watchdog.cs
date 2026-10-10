using System.Diagnostics;
using FanControlApp.Infrastructure;

namespace FanControlApp.Cooling;

/// <summary>
/// A second copy of this exe that grabs the fans first (only the first grabber holds the real BIOS settings).
/// It always hands them back to the BIOS when the app dies, however it dies; restoring twice costs nothing.
/// </summary>
public static class Watchdog
{
    public const string Flag = "--watchdog";

    private const int ParentPollMs = 300;

    // Wakes the sentinel to hand back before Windows quits it at sign-out/shutdown.
    private static readonly ManualResetEvent SessionEnd = new(false);

    // Set whenever the BIOS has the fans, so session end knows the process may go.
    private static readonly ManualResetEvent HandedBack = new(false);

    private enum Wake { Signal, ParentGone, SessionEnd }

    /// <summary>Windows is ending the session: hand the fans back now, waiting up to the timeout.</summary>
    public static bool ReleaseForSessionEnd(TimeSpan timeout)
    {
        SessionEnd.Set();
        return HandedBack.WaitOne(timeout);
    }

    /// <summary>Start the sentinel and block until it holds the fans; never write a fan before this returns.</summary>
    public static WatchdogLink? LaunchAndWait(IEnumerable<string> fanNames, TimeSpan timeout)
    {
        string[] fans = fanNames.ToArray();
        if (fans.Length == 0) return null;

        WatchdogLink? link = null;
        try
        {
            int pid = Environment.ProcessId;
            link = WatchdogLink.Create(pid);

            string? exe = Environment.ProcessPath;
            if (exe == null)
            {
                DebugLog.Write("Watchdog NOT started: no process path.");
                link.Dispose();
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add(Flag);
            psi.ArgumentList.Add(pid.ToString());
            foreach (string f in fans) psi.ArgumentList.Add(f);

            Process? p = Process.Start(psi);
            link.Sentinel = p;
            DebugLog.Write($"Watchdog starting (pid {p?.Id.ToString() ?? "?"}), guarding " +
                           $"[{string.Join(", ", fans)}]; waiting for it to take the fans.");

            if (link.Ready.WaitOne(timeout))
            {
                DebugLog.Write("Watchdog holds the fans. It owns releasing them from here.");
                return link;
            }

            DebugLog.Write("Watchdog did not take the fans in time - falling back to " +
                           "app-owned release. Graceful exits still restore the BIOS; " +
                           "a force-kill would leave the fans where they are.");
            link.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Watchdog failed to start - falling back to app-owned release.", ex);
            link?.Dispose();
            return null;
        }
    }

    /// <summary>The windowless sentinel: take the fans, signal Ready, hand back when the app asks or dies.</summary>
    public static void RunSentinel(string[] args)
    {
        try { RunSentinelCore(args); }
        finally { HandedBack.Set(); } // holding nothing, so session end needn't wait
    }

    private static void RunSentinelCore(string[] args)
    {
        // args: --watchdog <parentPid> <fan name> [<fan name> ...]
        if (args.Length < 3 || !int.TryParse(args[1], out int parentPid))
        {
            DebugLog.Write("[watchdog] Bad arguments; exiting.");
            return;
        }

        string[] names = args.Skip(2).ToArray();
        DebugLog.Write($"[watchdog] Guarding pid {parentPid} / [{string.Join(", ", names)}].");

        WatchdogLink link;
        try
        {
            link = WatchdogLink.Open(parentPid);
        }
        catch (Exception ex)
        {
            DebugLog.Write("[watchdog] Could not attach to the app's signals; exiting.", ex);
            return;
        }

        using (link)
        using (var hw = new HardwareMonitor())
        {
            try
            {
                hw.Open();
            }
            catch (Exception ex)
            {
                DebugLog.Write("[watchdog] Could not open the hardware; exiting.", ex);
                return;
            }

            List<FanChannel> fans = names
                .Select(hw.FindFan)
                .Where(f => f is { CanControl: true })
                .Select(f => f!)
                .ToList();

            if (fans.Count == 0)
            {
                DebugLog.Write("[watchdog] None of the named fans are controllable; exiting.");
                return;
            }

            // Trim idle memory so the waiting sentinel doesn't look like a second full app.
            Infrastructure.WorkingSet.Trim();

            Loop(link, hw, fans, parentPid);
        }

        DebugLog.Write("[watchdog] Done.");
    }

    private static void Loop(WatchdogLink link, HardwareMonitor hw, List<FanChannel> fans, int parentPid)
    {
        while (true)
        {
            Seize(hw, fans);
            HandedBack.Reset();
            link.Ready.Set();

            // Hold the fans until the app asks for them back, dies, or Windows ends the session.
            Wake why = WaitFor(link.Restore, parentPid);
            link.Ready.Reset();
            RestoreToBios(fans);
            HandedBack.Set();

            if (why == Wake.ParentGone)
            {
                DebugLog.Write($"[watchdog] Parent {parentPid} is gone - fans are back on the BIOS curve.");
                return;
            }
            if (why == Wake.SessionEnd)
            {
                DebugLog.Write("[watchdog] Windows session ending - fans are back on the BIOS curve.");
                return;
            }

            DebugLog.Write("[watchdog] App asked for the BIOS to take over; waiting to be told to resume.");

            // Paused: the BIOS has the fans, nothing to undo if the app dies.
            Wake paused = WaitFor(link.Resume, parentPid);
            if (paused == Wake.ParentGone)
            {
                DebugLog.Write($"[watchdog] Parent {parentPid} died while paused - fans already on the BIOS.");
                return;
            }
            if (paused == Wake.SessionEnd)
            {
                DebugLog.Write("[watchdog] Windows session ending while paused - fans already on the BIOS.");
                return;
            }

            DebugLog.Write("[watchdog] Taking the fans again.");
        }
    }

    /// <summary>Take the headers at their current speed so this process records the pristine BIOS settings.</summary>
    private static void Seize(HardwareMonitor hw, List<FanChannel> fans)
    {
        hw.Refresh();

        foreach (FanChannel f in fans)
        {
            try
            {
                float current = f.Percent ?? 50f;
                f.SetPercent(current);
                DebugLog.Write($"[watchdog] Took '{f.Name}' at {current:F1}% - BIOS settings recorded.");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[watchdog] Could not take '{f.Name}'.", ex);
            }
        }
    }

    private static void RestoreToBios(List<FanChannel> fans)
    {
        foreach (FanChannel f in fans)
        {
            try
            {
                f.Release();
                DebugLog.Write($"[watchdog] '{f.Name}' handed back to the BIOS.");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[watchdog] Could not hand back '{f.Name}'.", ex);
            }
        }

        // Let the Super I/O latch the restored mode before anything else touches it.
        Thread.Sleep(200);
    }

    /// <summary>Wait for a signal, while watching for the parent dying or the session ending.</summary>
    private static Wake WaitFor(EventWaitHandle signal, int parentPid)
    {
        WaitHandle[] handles = { signal, SessionEnd };
        while (true)
        {
            int hit = WaitHandle.WaitAny(handles, ParentPollMs);
            if (hit == 0) return Wake.Signal;
            if (hit == 1) return Wake.SessionEnd;
            if (IsGone(parentPid)) return Wake.ParentGone;
        }
    }

    private static bool IsGone(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
