// The timer tick and the per second poll that decides the fan speed.
using System.Timers;
using FanControlApp.Infrastructure;
using Timer = System.Timers.Timer;

namespace FanControlApp.Cooling;

public sealed partial class FanController
{
    // ---- the loop -----------------------------------------------------------

    // 1 while inside Poll(); a stalled read skips ticks since two threads in the sensor library is undefined.
    private int _tickBusy;

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _tickBusy, 1, 0) != 0)
            return;

        try
        {
            // A queued tick can land after Dispose - it must never touch hardware.
            if (System.Threading.Volatile.Read(ref _disposedFlag) != 0) return;
            Poll();
        }
        catch (Exception ex)
        {
            // We can no longer trust our readings.
            DebugLog.Write("Tick failed - handing the fans back to the BIOS.", ex);
            SafeRelease();
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _tickBusy, 0);
        }
    }

    private void Poll()
    {
        _hw.Refresh();
        CaptureLoads(); // before ANY peak tracking, so log and display agree

        List<FanChannel> controlled;
        WatchdogLink? link;
        bool paused;
        lock (_gate)
        {
            controlled = _controlled.ToList();
            link = _link;
            paused = _paused;
        }

        float? cpu = _hw.CpuTemp;
        float? gpu = _hw.GpuTemp;

        // Whichever is hotter.
        float? source = Max(cpu, gpu);

        if (controlled.Count == 0)
        {
            Publish(cpu, gpu, source, noFans: true, biosHasFans: true,
                    status: "No controllable fans found - the BIOS is running your fans. See fan_debug.log.");
            return;
        }

        // A dead sentinel still looks fine (events outlive it), so fall back to owning the release ourselves.
        if (link != null && !link.SentinelAlive)
        {
            DebugLog.Write(
                "!! Watchdog process is GONE. Falling back to app-owned release. " +
                "Our own saved defaults are the state the watchdog seized (the BIOS's " +
                "idle speed), not the live BIOS curve - so a clean exit now parks the " +
                "fans at a fixed safe speed rather than restoring the curve. " +
                "Reboot to get the BIOS curve back.");

            lock (_gate) _link = null;
            link = null;
            _sentinelLost = true;
        }

        if (paused)
        {
            // Not writing, so no conflict to win.
            if (_conflict)
            {
                _conflict = false;
                _foreignTicks = 0;
                SetTickRate(TickMs);
                DebugLog.Write("Paused during a conflict - conflict state cleared.");
            }
            // Keep logging what the BIOS does.
            TrackPeaks(cpu, gpu, controlled);
            if (Environment.TickCount64 - _lastSampleLogMs >= SampleEveryMs)
            {
                _lastSampleLogMs = Environment.TickCount64;
                LogSample(cpu, gpu, source, controlled, bios: true);
            }

            Publish(cpu, gpu, source, biosHasFans: true,
                    status: "Paused - the BIOS curve has your fans.");
            return;
        }

        // No temperature, no decision: hand back to the BIOS.
        if (source is not { } temp || float.IsNaN(temp))
        {
            _blindTicks++;
            bool handedBack = _blindTicks >= MaxBlindTicks;
            // The watchdog holds the fans from startup, so a blind start must hand back too.
            bool held = _engaged || (link != null && link.Ready.WaitOne(0));
            if (handedBack && held)
            {
                DebugLog.Write($"No temperature for {_blindTicks} ticks - handing the fans back.");
                HandBackToBios();
            }

            Publish(cpu, gpu, null, biosHasFans: handedBack,
                    status: handedBack ? "No temperature reading - BIOS has the fans."
                                       : "No temperature reading - handing the fans to the BIOS...");
            return;
        }

        _blindTicks = 0;

        // Drive off the window's hottest reading; spikes land instantly, only quietening waits.
        string src = (gpu ?? float.MinValue) >= (cpu ?? float.MinValue) ? "GPU" : "CPU";
        (float driving, string drivingSrc) = HoldPeak(temp, src);

        // Never write before the watchdog holds the headers, or it has no BIOS state to restore.
        if (link != null && !link.Ready.WaitOne(0))
        {
            link.Resume.Set();
            Publish(cpu, gpu, temp,
                    status: "Waiting for the watchdog to take the fans...");
            return;
        }

        // This tick's interval, read before DetectForeignWriter can change it.
        float dt = (float)(_timer.Interval / 1000.0);

        // Check the chip still holds our last write; if another app (often RGB suites) wrote, out-write it.
        DetectForeignWriter(controlled);

        // fan % = temperature plus the hot lean, floored and capped.
        float lean = Math.Clamp(driving - HotLeanFromC, 0f, HotLeanMax);
        float desired = Math.Clamp(driving + lean, FloorPercent, CeilingPercent);
        _currentPercent = Slew(_currentPercent, desired, dt);

        foreach (FanChannel f in controlled)
            f.SetPercent(_currentPercent);

        _engaged = true;

        // Label with the chip that reached the held peak, not the instant hotter one.
        string status = $"Matching {drivingSrc} {driving:F0}°C -> Fans: {_currentPercent:F0}%";
        if (_conflict)
            status += "   ·   another app is fighting for the fans - holding control";

        TrackPeaks(cpu, gpu, controlled);

        _tickCount++;
        if (Environment.TickCount64 - _lastSampleLogMs >= SampleEveryMs)
        {
            _lastSampleLogMs = Environment.TickCount64;
            LogSample(cpu, gpu, temp, controlled, drivingTemp: driving);
        }

        Publish(cpu, gpu, driving, status: status);
    }

    private float Slew(float current, float desired, float dt)
    {
        // dt keeps %/second ramps the same at any tick rate.
        float up = SlewUpPerSec * dt;
        float down = SlewDownPerSec * dt;

        float delta = desired - current;
        if (delta > up) delta = up;
        if (delta < -down) delta = -down;
        return current + delta;
    }

    private static float? Max(float? a, float? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return MathF.Max(a.Value, b.Value);
    }
}
