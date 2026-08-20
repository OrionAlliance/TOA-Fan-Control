using System.Timers;
using FanControlApp.Infrastructure;
using Timer = System.Timers.Timer;

namespace FanControlApp.Cooling;

/// <summary>A snapshot of one poll, handed to the UI to display.</summary>
public sealed class FanReadings
{
    public float? CpuTemp { get; init; }
    public float? GpuTemp { get; init; }

    /// <summary>While driving: the held recent peak (see PeakHoldSeconds) the fans
    /// are matching. Otherwise the current hotter of CPU/GPU, or null with no reading.</summary>
    public float? SourceTemp { get; init; }
    public float OutputPercent { get; init; }

    /// <summary>Session peaks (since app start / last Reset peaks) - the ONE truth
    /// every view renders, so dials, bars and Game Mode always agree.</summary>
    public float PeakCpu { get; init; } = float.NaN;
    public float PeakGpu { get; init; } = float.NaN;

    /// <summary>Session peak load % - its own maximum, independent of peak temp.</summary>
    public float PeakCpuLoad { get; init; } = float.NaN;
    public float PeakGpuLoad { get; init; } = float.NaN;

    /// <summary>Live load % right now (latest ~1s capture) - the cyan triangle's needle.</summary>
    public float CpuLoad { get; init; } = float.NaN;
    public float GpuLoad { get; init; } = float.NaN;

    /// <summary>True = GPU markers show real load (watts vs card max); false = busy-time
    /// fallback. Drives the tooltip wording - the gauge must say what it measures.</summary>
    public bool GpuLoadIsTrue { get; init; }

    /// <summary>Nothing to drive - the app is a read-only thermometer right now.</summary>
    public bool NoControllableFans { get; init; }

    /// <summary>The watchdog died mid-session; a clean exit can no longer restore the BIOS curve.</summary>
    public bool SentinelLost { get; init; }

    /// <summary>Another program is writing fan speeds too; we're out-writing it.</summary>
    public bool Conflict { get; init; }

    public string Status { get; init; } = "";
    public IReadOnlyList<FanChannel> Fans { get; init; } = Array.Empty<FanChannel>();

    /// <summary>Names of the fans the app is actually driving - what the UI shows as dials.</summary>
    public IReadOnlyList<string> DrivenFans { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The whole engine, in one sentence: every second, take the hotter of the CPU
/// and GPU and set the fans to that percent - leaning up to +5 ahead of it past
/// 70C. 65C -> 65%, 78C -> 83%. That's it. No modes, no target, no curve - the
/// rule is its own safety, because hot automatically means fast.
///
/// Releasing is NOT this class's job when a watchdog is attached. The fan chip
/// has no "hand back to BIOS" command - the library restores a header by writing
/// back what it read the first time it took that header, so only the first
/// grabber holds the real BIOS settings. The watchdog grabs them before we do,
/// which makes it the only thing that can truly hand them back. We just ask.
///
/// Without a watchdog we're the first grabber, so we own the release ourselves.
/// </summary>
public sealed class FanController : IDisposable
{
    private const double TickMs = 1000;

    // When another program is fighting us for the fan registers, tick 4x as fast
    // so our value is re-asserted within 250ms of any foreign write - holding
    // control by persistence, since the Super I/O has no concept of ownership.
    private const double ConflictTickMs = 250;

    // The one hard rule, and it is NOT a setting. 30% is the floor for every fan,
    // always: below it Chassis Fan #2 stalls to 0 RPM (measured), and a stalled
    // fan means no airflow. Making it a constant means no config edit or bug can
    // ever drop a fan below its stall point - the exact mistake that stalled #2
    // during testing when the floor was briefly a tunable 20%.
    public const float FloorPercent = 30f;
    public const float CeilingPercent = 100f;

    // Hot lean: past 70C fans run up to +5 ahead of the temp, faded in by 75C so the target never steps at a boundary.
    private const float HotLeanFromC = 70f;
    private const float HotLeanMax = 5f;

    // Ramp up eagerly, coast down gently. Fast down-ramps are what make fan
    // control audibly "pulse", and being slow to quieten costs nothing.
    // Per SECOND, not per tick - the tick rate changes during a conflict, and
    // faster ticks must not mean faster ramps.
    private const float SlewUpPerSec = 8f;
    private const float SlewDownPerSec = 3f;

    // Fans track the hottest reading over this many seconds, not the instant's.
    // Bursty loads (video encoding) sawtooth the CPU temp; holding the recent
    // peak keeps the fans steady instead of surfing every spike and dip.
    private const float PeakHoldSeconds = 15f;

    private const int MaxBlindTicks = 3;

    // Conflict detection: the chip reporting a duty that differs from what we
    // last commanded means someone else wrote it. Tolerance covers the chip's
    // 0-255 PWM quantization; N consecutive misses avoids one-off flukes.
    private const float ForeignWriteTolerance = 2.5f;
    private const int ForeignTicksToConfirm = 3;
    private const long ConflictClearMs = 30_000;

    // A SAMPLE line every 5s (time-based - tick rate varies during conflicts).
    private const long SampleEveryMs = 5_000;

    private readonly object _gate = new();
    private readonly HardwareMonitor _hw = new();
    private readonly Timer _timer = new(TickMs);

    private FanSettings _settings;
    private WatchdogLink? _link;
    private List<FanChannel> _controlled = new();
    private List<FanChannel> _candidates = new();
    private float _currentPercent = FloorPercent;
    // Rolling peak-hold of the driving temp over PeakHoldSeconds (see the tick).
    // Src remembers which chip each sample came from, so the status line always
    // attributes the held peak to the chip that actually reached it.
    private readonly Queue<(long Ms, float Temp, string Src)> _peakWindow = new();

    // Last tick's raw temp - a sample must appear on 2 consecutive ticks to
    // enter the hold window (glitch guard; see the tick).
    private float _prevWindowTemp = float.NaN;
    private float _drivingTemp;
    private bool _engaged;
    private bool _paused;
    private int _blindTicks;
    private int _disposedFlag; // Interlocked - Dispose can race in from the UI thread and ProcessExit at once
    private bool _publishFaulted; // log-on-change gate for subscriber throws

    // Foreign-writer (SignalRGB & friends) tracking. Interval stamps are
    // monotonic (TickCount64) - a wall-clock jump must not fake or swallow one.
    private int _foreignTicks;
    private bool _conflict;
    private long _lastForeignWriteMs;
    private long _lastSampleLogMs;

    // Session telemetry. Without this the log records that the app ran and
    // nothing about what it did - useless for tuning, which is the whole reason
    // the log exists.
    private int _tickCount;
    private bool _sentinelLost;
    private float _peakCpu = float.NaN;
    private float _peakGpu = float.NaN;
    private float _peakCpuLoad = float.NaN;
    private float _peakGpuLoad = float.NaN;
    private float _peakOut;
    private readonly Dictionary<string, float> _peakRpm = new();

    // The peaks the UI shows (cleared by Reset peaks; the log's stay whole-session).
    private float _dispPeakCpu = float.NaN;
    private float _dispPeakGpu = float.NaN;
    private float _dispPeakCpuLoad = float.NaN;
    private float _dispPeakGpuLoad = float.NaN;

    // A load must hold 2 consecutive ~1s captures to count as effort - one-poll
    // bursts (our own view-switch render, background blips) can't warm anything.
    private float _prevCpuLoad = float.NaN;
    private float _prevGpuLoad = float.NaN;
    private float _susCpuLoad = float.NaN;
    private float _susGpuLoad = float.NaN;
    private long _lastLoadCaptureMs;
    private readonly long _startedMs = Environment.TickCount64;

    public event EventHandler<FanReadings>? Updated;

    // Hands out the LIVE instance - safe only while every mutation and every
    // multi-field read stays on the dispatcher thread (they all do; keep it so).
    public FanSettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    /// <summary>The headers we resolved and will write to - what the watchdog must guard.</summary>
    public IReadOnlyList<string> ControlledFanNames
    {
        get { lock (_gate) return _controlled.Select(f => f.Name).ToList(); }
    }

    /// <summary>
    /// Every fan the app COULD drive (controllable, passes the pump/CPU/GPU name
    /// rule), with a current RPM to help a person identify it - what the fan
    /// picker lists, regardless of what's currently selected.
    /// </summary>
    public IReadOnlyList<(string Name, float? Rpm)> CandidateFans
    {
        get { lock (_gate) return _candidates.Select(f => (f.Name, f.Rpm)).ToList(); }
    }

    public FanController(FanSettings settings)
    {
        _settings = settings;
        _timer.Elapsed += OnTick;
        _timer.AutoReset = true;
    }

    /// <summary>
    /// Open the hardware and work out what we can drive - but do NOT write to
    /// anything yet. The watchdog has to take the fans before we touch them.
    /// </summary>
    public void OpenHardware()
    {
        _hw.Open();

        // A user-entered max (for a card the library doesn't know) outranks the
        // library - but only while the same card is installed. A swap makes the
        // stored name mismatch and the number is ignored, never inherited.
        lock (_gate)
        {
            if (UserGpuOverrideFor() is { } w)
            {
                _hw.GpuMaxWatts = w;
                DebugLog.Write($"GPU max watts: user-set {w}W for '{_hw.GpuName}'.");
            }
        }

        ResolveControlledFans();
    }

    // The ONE precedence rule, written once: a user-entered max stands while the
    // same card is installed - library values never outrank it. Call under _gate.
    private int? UserGpuOverrideFor() =>
        _settings.GpuUserMaxWattsFor == _hw.GpuName ? _settings.GpuUserMaxWatts : null;

    /// <summary>Same rule, for the notice flow's branch logic.</summary>
    public bool GpuUserOverrideActive { get { lock (_gate) return UserGpuOverrideFor() != null; } }

    /// <summary>Apply a just-entered user max immediately - no restart needed.</summary>
    public void SetGpuMaxWattsOverride(int watts)
    {
        if (_hw.GpuMaxWatts != watts) ResetGpuLoadStream();
        _hw.GpuMaxWatts = watts;
        DebugLog.Write($"GPU max watts: user-set {watts}W for '{_hw.GpuName}' (live).");
    }

    /// <summary>Re-match the card after a library fetch - a fresh install gets
    /// true load the moment the library lands, and a corrected (or removed) row
    /// takes effect without a restart instead of lying until one.</summary>
    public void RefreshGpuMaxFromLibrary()
    {
        lock (_gate)
        {
            // A user-entered value stands.
            if (UserGpuOverrideFor() != null) return;
        }
        int? max = GpuLibrary.MaxWattsFor(_hw.GpuName);
        if (_hw.GpuMaxWatts == max) return;
        ResetGpuLoadStream();
        _hw.GpuMaxWatts = max;
        DebugLog.Write(max is { } m
            ? $"GPU library match (post-fetch): '{_hw.GpuName}' = {m}W reference max."
            : $"GPU library (post-fetch): '{_hw.GpuName}' no longer listed - markers fall back to busy time.");
    }

    public void AttachWatchdog(WatchdogLink? link)
    {
        lock (_gate) _link = link;

        DebugLog.Write(link != null
            ? "Watchdog attached - it owns handing the fans back."
            : "No watchdog - this app owns handing the fans back. Graceful exits are " +
              "covered; a force-kill would leave the fans where they are.");
    }

    public void BeginControl()
    {
        _currentPercent = FloorPercent;
        _timer.Start();
        DebugLog.Write("Controller started.");
    }

    // Fans we must NOT drive with case-fan logic, matched by name (case-insensitive
    // substring). Everything else on the board is a case/system fan and is safe to
    // drive - however many there are.
    //   pump    - a liquid-cooler pump has to run flat-out; throttle it and the CPU
    //             cooks. The one genuine landmine.
    //   cpu     - the CPU cooler stays on the BIOS as a failsafe: no failure of this
    //             app can then starve the CPU of cooling.
    //   chipset - cools its own chip on its own temperature, not the CPU/GPU we track.
    //   gpu     - the GPU's own fan, driven by the GPU's driver, not us.
    private static readonly string[] SkipFanPatterns = { "pump", "cpu", "chipset", "gpu" };

    private void ResolveControlledFans()
    {
        lock (_gate)
        {
            _candidates = new List<FanChannel>();
            var skipped = new List<string>();

            foreach (FanChannel f in _hw.Fans)
            {
                if (!f.CanControl) continue; // read-only reading or an empty header

                string name = f.Name.ToLowerInvariant();
                if (SkipFanPatterns.Any(p => name.Contains(p)))
                {
                    skipped.Add(f.Name);
                    continue;
                }

                _candidates.Add(f);
            }

            // The user's picker can only NARROW the safety rule, never widen it:
            // it filters the candidates, and pump/CPU/GPU names never got that far.
            // Null = never picked (first run drives all candidates until then).
            List<string>? picked = _settings.SelectedFans;
            _controlled = picked == null
                ? _candidates.ToList()
                : _candidates.Where(f =>
                        picked.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                    .ToList();

            var unchecked_ = _candidates.Except(_controlled).Select(f => f.Name).ToList();
            DebugLog.Write(
                $"Driving: [{string.Join(", ", _controlled.Select(f => f.Name))}]  " +
                $"(left on BIOS: [{string.Join(", ", skipped)}]" +
                (unchecked_.Count > 0 ? $", unchecked by user: [{string.Join(", ", unchecked_)}]" : "") +
                ")");
        }
    }

    /// <param name="reresolve">False = save only, don't touch the driven set. Used
    /// when a fan-selection change must wait for the next launch: the watchdog
    /// guards the set it seized at startup, and only that set.</param>
    public void UpdateSettings(Action<FanSettings> mutate, bool reresolve = true)
    {
        lock (_gate)
        {
            mutate(_settings);
            SettingsStore.Save(_settings);
        }
        if (reresolve) ResolveControlledFans();
    }

    // ---- pause / resume -----------------------------------------------------

    /// <summary>Clear the displayed session peaks - every view resets at once.</summary>
    public void ResetDisplayPeaks()
    {
        _dispPeakCpu = float.NaN;
        _dispPeakGpu = float.NaN;
        _dispPeakCpuLoad = float.NaN;
        _dispPeakGpuLoad = float.NaN;
    }

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
            // No watchdog, so we were the first to take these headers and our own
            // saved defaults are the real ones.
            foreach (FanChannel f in controlled)
            {
                try { f.Release(); }
                catch (Exception ex) { DebugLog.Write($"Release failed for '{f.Name}'.", ex); }
            }
        }

        _engaged = false;
    }

    // ---- the loop -----------------------------------------------------------

    // 1 while a tick is inside Poll(). A stalled hardware read must not let the
    // next timer tick pile on top of it - two threads in the sensor library at
    // once is undefined behaviour. Skipping a beat is harmless: the fans just
    // hold their speed ~1s longer and the next tick catches up.
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
            // An exception here means we can no longer trust our own readings.
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

        // The whole decision: whichever is hotter.
        float? source = Max(cpu, gpu);

        if (controlled.Count == 0)
        {
            Publish(cpu, gpu, source, noFans: true,
                    status: "No controllable fans found - the BIOS is running your fans. See fan_debug.log.");
            return;
        }

        // If the sentinel died, everything still LOOKS fine - the events outlive it,
        // Ready stays signalled, Restore gets set for nobody. Left alone we'd keep
        // driving and have no way at all to hand the fans back, which is worse than
        // never having had a watchdog. Fall back to owning the release ourselves.
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
            // Paused = not writing, so there is no conflict left to win.
            if (_conflict)
            {
                _conflict = false;
                _foreignTicks = 0;
                SetTickRate(TickMs);
                DebugLog.Write("Paused during a conflict - conflict state cleared.");
            }
            // Paused is not blind: the log keeps recording what the BIOS does.
            TrackPeaks(cpu, gpu, controlled);
            if (Environment.TickCount64 - _lastSampleLogMs >= SampleEveryMs)
            {
                _lastSampleLogMs = Environment.TickCount64;
                LogSample(cpu, gpu, source, controlled, bios: true);
            }

            Publish(cpu, gpu, source,
                    status: "Paused - the BIOS curve has your fans.");
            return;
        }

        // No temperature means no basis for a decision. Hand back to the BIOS.
        if (source is not { } temp || float.IsNaN(temp))
        {
            _blindTicks++;
            if (_blindTicks >= MaxBlindTicks && _engaged)
            {
                DebugLog.Write($"No temperature for {_blindTicks} ticks - handing the fans back.");
                HandBackToBios();
            }

            Publish(cpu, gpu, null,
                    status: "No temperature reading - BIOS has the fans.");
            return;
        }

        _blindTicks = 0;

        // Peak-hold: drive off the hottest reading in the last PeakHoldSeconds,
        // not this instant's. Bursty loads (video encoding) sawtooth the CPU temp;
        // matched 1:1 the fans would surf it. Holding the recent peak keeps them
        // steady, and the spike still lands instantly - only quietening waits.
        long nowMs = Environment.TickCount64;
        string src = (gpu ?? float.MinValue) >= (cpu ?? float.MinValue) ? "GPU" : "CPU";

        // A fresh engagement drives from the CURRENT temp: a peak held from
        // before a pause or sensor loss must not outlive its disengagement.
        if (!_engaged)
        {
            _peakWindow.Clear();
            _prevWindowTemp = float.NaN;
        }

        // A reading enters the hold only after surviving 2 consecutive ticks -
        // one glitched sample must not rule the fans for a whole window. The
        // instant reading still seeds the fold below, so a real spike lands
        // this tick; only its 15s hold starts one tick late.
        float paired = float.IsNaN(_prevWindowTemp) ? temp : MathF.Min(temp, _prevWindowTemp);
        _peakWindow.Enqueue((nowMs, paired, src));
        _prevWindowTemp = temp;

        long cutoff = nowMs - (long)(PeakHoldSeconds * 1000f);
        while (_peakWindow.Peek().Ms < cutoff) _peakWindow.Dequeue();
        var held = (Ms: nowMs, Temp: temp, Src: src);
        foreach (var e in _peakWindow) if (e.Temp > held.Temp) held = e;
        float driving = held.Temp;
        _drivingTemp = driving;

        // Never write before the watchdog holds these headers. If we got in first
        // it would be left with nothing to restore, and a force-kill would strand
        // the fans - the exact thing it exists to prevent.
        if (link != null && !link.Ready.WaitOne(0))
        {
            link.Resume.Set();
            Publish(cpu, gpu, temp,
                    status: "Waiting for the watchdog to take the fans...");
            return;
        }

        // dt = the interval that scheduled THIS tick, read before DetectForeignWriter can change it.
        float dt = (float)(_timer.Interval / 1000.0);

        // Before this tick's write: is the chip still holding what WE wrote last
        // tick? If not, another program (RGB suites often ship fan control and
        // enable it in updates) is writing too. Answer: out-write it - tick 4x
        // faster so our value is re-asserted within 250ms of every foreign write.
        DetectForeignWriter(controlled);

        // fan % = temperature, floored so no fan stalls and capped at full.
        // Past 70C the fans lean ahead: 72C -> 74%, 75C -> 80%, 85C -> 90%.
        float lean = Math.Clamp(driving - HotLeanFromC, 0f, HotLeanMax);
        float desired = Math.Clamp(driving + lean, FloorPercent, CeilingPercent);
        _currentPercent = Slew(_currentPercent, desired, dt);

        foreach (FanChannel f in controlled)
            f.SetPercent(_currentPercent);

        _engaged = true;

        // Label the held peak with the chip that actually reached it - the instant
        // hotter can be the OTHER chip while a held spike still drives the fans.
        string status = $"Matching {held.Src} {driving:F0}°C -> Fans: {_currentPercent:F0}%";
        if (_conflict)
            status += "   ·   another app is fighting for the fans - holding control";

        TrackPeaks(cpu, gpu, controlled);

        _tickCount++;
        if (Environment.TickCount64 - _lastSampleLogMs >= SampleEveryMs)
        {
            _lastSampleLogMs = Environment.TickCount64;
            LogSample(cpu, gpu, temp, controlled);
        }

        Publish(cpu, gpu, driving, status: status);
    }

    /// <summary>
    /// Compare the chip's reported duty against what we last commanded. A
    /// persistent mismatch (while we're engaged) means a foreign writer; enter
    /// conflict mode - 4x tick rate - until it's been quiet for 30 seconds.
    /// </summary>
    private void DetectForeignWriter(List<FanChannel> controlled)
    {
        if (!_engaged) return; // nothing of ours on the chip yet to compare against

        bool foreign = controlled.Any(f =>
            f.Percent is { } p && Math.Abs(p - _currentPercent) > ForeignWriteTolerance);

        if (foreign)
        {
            _lastForeignWriteMs = Environment.TickCount64;
            _foreignTicks++;

            if (!_conflict && _foreignTicks >= ForeignTicksToConfirm)
            {
                _conflict = true;
                SetTickRate(ConflictTickMs);
                DebugLog.Write(
                    "!! Another program is writing fan speeds over ours (chip readback " +
                    "disagrees with our command). Holding control at 4x re-assert rate. " +
                    "RGB suites (SignalRGB, iCUE, ...) often enable fan control in updates.");
            }
        }
        else
        {
            _foreignTicks = 0;

            if (_conflict && Environment.TickCount64 - _lastForeignWriteMs > ConflictClearMs)
            {
                _conflict = false;
                SetTickRate(TickMs);
                DebugLog.Write("Foreign fan writes stopped - back to the normal tick rate.");
            }
        }
    }

    // Stop/Start dodges the assign-Interval-inside-Elapsed Timers.Timer quirk; no-op once disposed.
    private void SetTickRate(double ms)
    {
        if (System.Threading.Volatile.Read(ref _disposedFlag) != 0) return;
        _timer.Stop();
        _timer.Interval = ms;
        _timer.Start();
    }

    // "Track the max, NaN-safe" - the one fold every peak in this file uses.
    private static void MaxInto(ref float peak, float v)
    {
        if (!float.IsNaN(v) && (float.IsNaN(peak) || v > peak)) peak = v;
    }

    /// <summary>True when the GPU markers show real load (watts vs the card's max)
    /// rather than busy time - the card has a power sensor AND a known max. Sensor
    /// PRESENCE, not this tick's value, so the label can't flicker on a blip.</summary>
    public bool GpuLoadIsTrue => _hw.GpuMaxWatts != null && _hw.GpuHasPowerSensor;

    /// <summary>The card's sensor-reported name - for the library notice popup.</summary>
    public string? GpuName => _hw.GpuName;

    /// <summary>Whether the card has a power sensor - the notice popup must not
    /// ask for max watts it can never use.</summary>
    public bool GpuHasPowerSensor => _hw.GpuHasPowerSensor;

    // Sustained load = min of the last two ~1s captures, so a one-poll blip can't
    // become a peak. Captured on its own ~1s cadence, NOT per tick - conflict
    // mode's 250ms ticks must not shrink the two-capture window to half a second.
    private void CaptureLoads()
    {
        if (Environment.TickCount64 - _lastLoadCaptureMs < 900) return;
        _lastLoadCaptureMs = Environment.TickCount64;

        // CPU load is time-based busy - honest as-is for a CPU (no readable
        // per-chip power ceiling exists to do better).
        float curCl = _hw.CpuLoad ?? float.NaN;

        // GPU: TRUE LOAD when possible - watts against the card's reference max
        // (the trailer up the hill, not the revs). Falls back to the D3D engine
        // counters (busy time) when the card lacks a power sensor or library row.
        // The mode is decided by sensor PRESENCE, not this tick's value: a blip
        // must poison the pair (NaN), never slip a busy sample into the stream.
        float curGl;
        if (_hw.GpuHasPowerSensor && _hw.GpuMaxWatts is { } max && max > 0)
            curGl = _hw.GpuPowerW is { } watts ? MathF.Min(watts / max * 100f, 100f) : float.NaN;
        else
            curGl = _hw.GpuEngineLoad ?? float.NaN;

        _susCpuLoad = MathF.Min(curCl, _prevCpuLoad); // NaN or a blip poisons the pair
        _susGpuLoad = MathF.Min(curGl, _prevGpuLoad);
        _prevCpuLoad = curCl;
        _prevGpuLoad = curGl;
    }

    // The GPU load measure just switched (busy time <-> true load, or a new
    // denominator): samples of the old measure must not survive as "peaks".
    private void ResetGpuLoadStream()
    {
        _prevGpuLoad = float.NaN;
        _susGpuLoad = float.NaN;
        _peakGpuLoad = float.NaN;
        _dispPeakGpuLoad = float.NaN;
    }

    private void TrackPeaks(float? cpu, float? gpu, List<FanChannel> controlled)
    {
        MaxInto(ref _peakCpu, cpu ?? float.NaN);
        MaxInto(ref _peakGpu, gpu ?? float.NaN);
        MaxInto(ref _peakCpuLoad, _susCpuLoad);
        MaxInto(ref _peakGpuLoad, _susGpuLoad);
        if (_currentPercent > _peakOut) _peakOut = _currentPercent;

        foreach (FanChannel f in controlled)
        {
            if (f.Rpm is not { } rpm) continue;
            if (!_peakRpm.TryGetValue(f.Name, out float best) || rpm > best)
                _peakRpm[f.Name] = rpm;
        }
    }

    private void LogSample(float? cpu, float? gpu, float? hotter, List<FanChannel> controlled, bool bios = false)
    {
        string rpm = string.Join(" ", controlled.Select(f => $"[{f.Name}={f.Rpm:F0}]"));
        if (_hw.CpuFan?.Rpm is { } cr) rpm += $" [{_hw.CpuFan.Name}={cr:F0}]";
        if (_hw.GpuFan?.Rpm is { } gr) rpm += $" [{_hw.GpuFan.Name}={gr:F0}]";
        string cl = _hw.CpuLoad is { } c ? $"@{c:F0}%" : "";
        string gl = _hw.GpuEngineLoad is { } g ? $"@{g:F0}%" : "";
        string gc = _hw.GpuCoreClockMhz is { } k ? $" gclk={k:F0}" : "";
        string gp = _hw.GpuPowerW is { } pw
            ? $" gpw={pw:F0}{(_hw.GpuMaxWatts is { } mx ? $"/{mx}" : "")}"
            : "";
        string bt = _hw.BoardTemp is { } b ? $" board={b:F1}" : "";
        // When the held peak is above the current hotter, show it - that's why
        // the fans sit where they do while the instantaneous temp has dipped.
        string hold = _drivingTemp > (hotter ?? float.MinValue) + 0.5f ? $" hold={_drivingTemp:F1}" : "";
        DebugLog.Write(bios
            ? $"SAMPLE(bios) cpu={cpu:F1}{cl} gpu={gpu:F1}{gl}{gc}{gp}{bt} hotter={hotter:F1} {rpm}"
            : $"SAMPLE cpu={cpu:F1}{cl} gpu={gpu:F1}{gl}{gc}{gp}{bt} hotter={hotter:F1}{hold} out={_currentPercent:F1}% {rpm}");
    }

    private void LogSessionSummary()
    {
        if (_tickCount == 0) return;

        TimeSpan ran = TimeSpan.FromMilliseconds(Environment.TickCount64 - _startedMs);
        string rpm = string.Join(" ", _peakRpm.Select(kv => $"[{kv.Key}={kv.Value:F0}]"));
        string loads = "";
        if (!float.IsNaN(_peakCpuLoad)) loads += $" cpuLoad={_peakCpuLoad:F0}%";
        if (!float.IsNaN(_peakGpuLoad)) loads += $" gpuLoad={_peakGpuLoad:F0}%";

        DebugLog.Write(
            $"SESSION PEAKS after {ran.TotalMinutes:F1} min: " +
            $"cpu={_peakCpu:F1}C gpu={_peakGpu:F1}C{loads} maxOut={_peakOut:F0}% {rpm}");
    }

    private float Slew(float current, float desired, float dt)
    {
        // dt scales conflict mode's faster ticks to the same %/second ramp rates.
        float up = SlewUpPerSec * dt;
        float down = SlewDownPerSec * dt;

        float delta = desired - current;
        if (delta > up) delta = up;
        if (delta < -down) delta = -down;
        return current + delta;
    }

    private void Publish(float? cpu, float? gpu, float? source, string status, bool noFans = false)
    {
        // Display peaks live here, not in the views: every view renders these, so
        // switching dial/bar/Game Mode can never show different "peaks". Kept
        // separate from the SESSION PEAKS log values - Reset peaks clears these,
        // but the log keeps reporting the true whole-session maximum for support.
        MaxInto(ref _dispPeakCpu, cpu ?? float.NaN);
        MaxInto(ref _dispPeakGpu, gpu ?? float.NaN);
        MaxInto(ref _dispPeakCpuLoad, _susCpuLoad);
        MaxInto(ref _dispPeakGpuLoad, _susGpuLoad);

        var readings = new FanReadings
        {
            CpuTemp = cpu,
            GpuTemp = gpu,
            SourceTemp = source,
            OutputPercent = _currentPercent,
            PeakCpu = _dispPeakCpu,
            PeakGpu = _dispPeakGpu,
            PeakCpuLoad = _dispPeakCpuLoad,
            PeakGpuLoad = _dispPeakGpuLoad,
            CpuLoad = _prevCpuLoad,
            GpuLoad = _prevGpuLoad,
            GpuLoadIsTrue = GpuLoadIsTrue,
            NoControllableFans = noFans,
            SentinelLost = _sentinelLost,
            Conflict = _conflict,
            Status = _sentinelLost ? status + "   ·   WATCHDOG GONE - restart the app" : status,
            Fans = _hw.Fans,
            DrivenFans = ControlledFanNames,
        };

        // A synchronous subscriber throw must not unwind into Poll and cost fan control.
        try
        {
            Updated?.Invoke(this, readings);
        }
        catch (Exception ex)
        {
            if (!_publishFaulted)
            {
                _publishFaulted = true;
                DebugLog.Write("An Updated subscriber threw - suppressing repeats until it recovers.", ex);
            }
            return;
        }

        if (_publishFaulted)
        {
            _publishFaulted = false;
            DebugLog.Write("Updated subscribers recovered.");
        }
    }

    private static float? Max(float? a, float? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return MathF.Max(a.Value, b.Value);
    }

    /// <summary>
    /// Get the fans back on the BIOS curve. Safe to call repeatedly, from any
    /// thread, and during shutdown - it must never throw.
    /// </summary>
    public void SafeRelease()
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

        // Before anything else - if this throws or the release hangs, the numbers
        // from the session are still on disk.
        try { LogSessionSummary(); } catch { /* never block shutdown */ }

        SafeRelease();

        // Give the watchdog (or the Super I/O) a moment to actually put the fans
        // back before this process - and its driver handle - goes away.
        Thread.Sleep(400);

        if (drained) _hw.Dispose();
        DebugLog.Write("Controller disposed.");
    }
}
