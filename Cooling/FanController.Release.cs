// Pause and resume, handing the fans back to the BIOS, and shutdown.
using System.Timers;
using FanControlApp.Infrastructure;
using Timer = System.Timers.Timer;

namespace FanControlApp.Cooling;

public sealed partial class FanController
{
    // ---- pause / resume -----------------------------------------------------

    /// <summary>Stop driving and put the fans back on the BIOS curve.</summary>
    public void Pause()
    {
        lock (_gate) _paused = true;
        HandBackToBios();
        DebugLog.Write("Paused - BIOS has the fans.");
    }

    /// <summary>Start driving again. The next tick re-takes the fans.</summary>
    public void Resume()
    {
        lock (_gate) _paused = false;
        DebugLog.Write("Resuming control.");
    }

    private void HandBackToBios()
    {
        WatchdogLink? link;
        List<FanChannel> controlled;
        lock (_gate)
        {
            link = _link;
            controlled = _controlled.ToList();
        }

        if (link != null)
        {
            // Only the watchdog knows the real BIOS settings - ask it.
            link.Restore.Set();
        }
        else
        {
            // No watchdog: we grabbed first, so our saved defaults are the real BIOS ones.
            foreach (FanChannel f in controlled)
            {
                try { f.Release(); }
                catch (Exception ex) { DebugLog.Write($"Release failed for '{f.Name}'.", ex); }
            }
        }

        _engaged = false;
    }

    /// <summary>Put the fans back on the BIOS curve; safe from any thread, anytime, and never throws.</summary>
    private void SafeRelease()
    {
        try
        {
            HandBackToBios();
        }
        catch (Exception ex)
        {
            DebugLog.Write("SafeRelease failed.", ex);
        }
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

        try { _timer.Stop(); } catch { /* shutting down */ }

        // Drain the in-flight tick (2s cap) before the hardware goes away under it.
        bool drained = false;
        for (int i = 0; i < 200; i++)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _tickBusy, 0, 0) == 0) { drained = true; break; }
            Thread.Sleep(10);
        }
        if (!drained) DebugLog.Write("A tick is still stuck in a hardware read after 2s - skipping hardware close.");

        try { _timer.Dispose(); } catch { /* shutting down */ }

        // First, so the session numbers reach disk even if the release hangs.
        try { LogSessionSummary(); } catch { /* never block shutdown */ }

        SafeRelease();

        // Let the fans actually go back before our driver handle goes away.
        Thread.Sleep(400);

        // Closing the library restores OUR saved defaults (the watchdog's seized speed) over its BIOS hand-back.
        bool watchdogOwns;
        lock (_gate) watchdogOwns = _link != null;
        if (watchdogOwns) DebugLog.Write("Watchdog owns the hand-back - leaving the hardware open so it isn't overwritten.");
        else if (drained) _hw.Dispose();
        DebugLog.Write("Controller disposed.");
    }
}
