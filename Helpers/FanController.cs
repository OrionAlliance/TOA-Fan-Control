using System.Timers;
using Timer = System.Timers.Timer;

namespace FanControlApp.Helpers;

/// <summary>A snapshot of one poll, handed to the UI to display.</summary>
public sealed class FanReadings
{
    public float? CpuTemp { get; init; }
    public float? GpuTemp { get; init; }
    public float? SourceTemp { get; init; }
    public float OutputPercent { get; init; }
    public FanMode Mode { get; init; }
    public bool Engaged { get; init; }
    public bool Panic { get; init; }

    /// <summary>Nothing to drive - the app is a read-only thermometer right now.</summary>
    public bool NoControllableFans { get; init; }

    /// <summary>The watchdog died mid-session; a clean exit can no longer restore the BIOS curve.</summary>
    public bool SentinelLost { get; init; }

    public string Status { get; init; } = "";
    public IReadOnlyList<FanChannel> Fans { get; init; } = Array.Empty<FanChannel>();
}

/// <summary>
/// Polls temps once a second and drives the controlled fan headers.
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

    // Ramp up eagerly, coast down gently. Fast down-ramps are what make fan
    // control audibly "pulse", and being slow to quieten costs nothing.
    private const float SlewUpPerTick = 8f;
    private const float SlewDownPerTick = 3f;

    // Auto mode: don't chase noise within this band of the target.
    private const float AutoDeadbandC = 1.5f;
    private const float AutoGain = 1.5f;
    private const float AutoMaxStepUp = 6f;
    private const float AutoMaxStepDown = 3f;

    private const int MaxBlindTicks = 3;

    // A line every 5s. Thermals move slowly, so this is plenty to tune from, and
    // it's ~200 lines for a 20-minute session - nothing next to the 2MB roll.
    private const int SampleEveryTicks = 5;

    private readonly object _gate = new();
    private readonly HardwareMonitor _hw = new();
    private readonly Timer _timer = new(TickMs);

    private FanSettings _settings;
    private WatchdogLink? _link;
    private List<FanChannel> _controlled = new();
    private float _currentPercent = 50f;
    private bool _engaged;
    private bool _paused;
    private int _blindTicks;
    private bool _disposed;

    // Session telemetry. Without this the log records that the app ran and
    // nothing about what it did - useless for tuning, which is the whole reason
    // the log exists.
    private int _tickCount;
    private bool _wasPanic;
    private bool _sentinelLost;
    private float _peakCpu = float.NaN;
    private float _peakGpu = float.NaN;
    private float _peakOut;
    private readonly Dictionary<string, float> _peakRpm = new();
    private readonly DateTime _startedAt = DateTime.Now;

    public event EventHandler<FanReadings>? Updated;

    public FanSettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    public HardwareMonitor Hardware => _hw;

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    /// <summary>True when the watchdog is holding the fans and owns handing them back.</summary>
    public bool WatchdogOwnsRelease
    {
        get { lock (_gate) return _link != null; }
    }

    /// <summary>The headers we resolved and will write to - what the watchdog must guard.</summary>
    public IReadOnlyList<string> ControlledFanNames
    {
        get { lock (_gate) return _controlled.Select(f => f.Name).ToList(); }
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
        ResolveControlledFans();
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
        _currentPercent = Math.Clamp(_settings.Curve.Evaluate(40f), _settings.MinPercent, _settings.MaxPercent);
        _timer.Start();
        DebugLog.Write("Controller started.");
    }

    private void ResolveControlledFans()
    {
        lock (_gate)
        {
            _controlled = new List<FanChannel>();
            foreach (string name in _settings.ControlledFans)
            {
                FanChannel? f = _hw.FindFan(name);
                if (f == null)
                {
                    DebugLog.Write($"Controlled fan '{name}' not found on this hardware - skipping.");
                    continue;
                }

                if (!f.CanControl)
                {
                    DebugLog.Write($"Controlled fan '{name}' is not writable - skipping.");
                    continue;
                }

                _controlled.Add(f);
            }

            DebugLog.Write($"Driving: [{string.Join(", ", _controlled.Select(f => f.Name))}]");
        }
    }

    public void UpdateSettings(Action<FanSettings> mutate)
    {
        lock (_gate)
        {
            mutate(_settings);
            SettingsStore.Save(_settings);
        }
        ResolveControlledFans();
    }

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

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        try
        {
            Poll();
        }
        catch (Exception ex)
        {
            // An exception here means we can no longer trust our own readings.
            DebugLog.Write("Tick failed - handing the fans back to the BIOS.", ex);
            SafeRelease();
        }
    }

    private void Poll()
    {
        _hw.Refresh();

        FanSettings s;
        List<FanChannel> controlled;
        WatchdogLink? link;
        bool paused;
        lock (_gate)
        {
            s = _settings;
            controlled = _controlled.ToList();
            link = _link;
            paused = _paused;
        }

        float? cpu = _hw.CpuTemp;
        float? gpu = _hw.GpuTemp;
        float? source = s.Source switch
        {
            TempSource.Cpu => cpu,
            TempSource.Gpu => gpu,
            _ => Max(cpu, gpu),
        };

        if (controlled.Count == 0)
        {
            Publish(cpu, gpu, source, controlled, panic: false, noFans: true,
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
            Publish(cpu, gpu, source, controlled, panic: false,
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

            Publish(cpu, gpu, null, controlled, panic: false,
                    status: "No temperature reading - BIOS has the fans.");
            return;
        }

        _blindTicks = 0;

        // Never write before the watchdog holds these headers. If we got in first
        // it would be left with nothing to restore, and a force-kill would strand
        // the fans - the exact thing it exists to prevent.
        if (link != null && !link.Ready.WaitOne(0))
        {
            link.Resume.Set();
            Publish(cpu, gpu, temp, controlled, panic: false,
                    status: "Waiting for the watchdog to take the fans...");
            return;
        }

        bool panic = temp >= s.PanicTemp;
        float desired;
        string status;

        if (panic)
        {
            // Safety overrides both modes, and skips the slew limiter entirely.
            desired = s.MaxPercent;
            _currentPercent = desired;
            status = $"PANIC - {temp:F0}C is at or over {s.PanicTemp:F0}C. Fans at full.";
        }
        else
        {
            desired = s.Mode switch
            {
                FanMode.Auto => ComputeAuto(temp, s),
                _ => Math.Clamp(s.Curve.Evaluate(temp), s.MinPercent, s.MaxPercent),
            };

            _currentPercent = Slew(_currentPercent, desired);
            status = s.Mode == FanMode.Auto
                ? $"Auto - holding {s.TargetTemp:F0}C (now {temp:F1}C)"
                : $"Manual - following the curve at {temp:F1}C";
        }

        foreach (FanChannel f in controlled)
            f.SetPercent(_currentPercent);

        _engaged = true;

        // A panic is rare and important - never let it be something you only find
        // out about by happening to be looking at the screen.
        if (panic && !_wasPanic)
            DebugLog.Write($"PANIC ENTERED at {temp:F1}C (limit {s.PanicTemp:F0}C) - fans to {s.MaxPercent:F0}%.");
        else if (!panic && _wasPanic)
            DebugLog.Write($"Panic over at {temp:F1}C - back to normal control.");
        _wasPanic = panic;

        TrackPeaks(cpu, gpu, controlled);

        if (++_tickCount % SampleEveryTicks == 0)
            LogSample(cpu, gpu, temp, s, controlled);

        Publish(cpu, gpu, temp, controlled, panic, status);
    }

    private void TrackPeaks(float? cpu, float? gpu, List<FanChannel> controlled)
    {
        if (cpu is { } c && (float.IsNaN(_peakCpu) || c > _peakCpu)) _peakCpu = c;
        if (gpu is { } g && (float.IsNaN(_peakGpu) || g > _peakGpu)) _peakGpu = g;
        if (_currentPercent > _peakOut) _peakOut = _currentPercent;

        foreach (FanChannel f in controlled)
        {
            if (f.Rpm is not { } rpm) continue;
            if (!_peakRpm.TryGetValue(f.Name, out float best) || rpm > best)
                _peakRpm[f.Name] = rpm;
        }
    }

    private void LogSample(float? cpu, float? gpu, float temp, FanSettings s, List<FanChannel> controlled)
    {
        string rpm = string.Join(" ", controlled.Select(f => $"[{f.Name}={f.Rpm:F0}]"));
        DebugLog.Write(
            $"SAMPLE mode={s.Mode} src={s.Source} cpu={cpu:F1} gpu={gpu:F1} " +
            $"driving={temp:F1} target={s.TargetTemp:F0} out={_currentPercent:F1}% {rpm}");
    }

    private void LogSessionSummary()
    {
        if (_tickCount == 0) return;

        TimeSpan ran = DateTime.Now - _startedAt;
        string rpm = string.Join(" ", _peakRpm.Select(kv => $"[{kv.Key}={kv.Value:F0}]"));

        DebugLog.Write(
            $"SESSION PEAKS after {ran.TotalMinutes:F1} min: " +
            $"cpu={_peakCpu:F1}C gpu={_peakGpu:F1}C maxOut={_peakOut:F0}% {rpm}");
    }

    /// <summary>
    /// Auto mode: nudge the fans until the temp settles at the target. Above the
    /// target it climbs, below it eases off, and inside the deadband it holds
    /// still so the fans don't hunt.
    /// </summary>
    private float ComputeAuto(float temp, FanSettings s)
    {
        float error = temp - s.TargetTemp;
        if (MathF.Abs(error) < AutoDeadbandC)
            return _currentPercent;

        float step = Math.Clamp(error * AutoGain, -AutoMaxStepDown, AutoMaxStepUp);
        return Math.Clamp(_currentPercent + step, s.MinPercent, s.MaxPercent);
    }

    private static float Slew(float current, float desired)
    {
        float delta = desired - current;
        if (delta > SlewUpPerTick) delta = SlewUpPerTick;
        if (delta < -SlewDownPerTick) delta = -SlewDownPerTick;
        return current + delta;
    }

    private void Publish(float? cpu, float? gpu, float? source, IReadOnlyList<FanChannel> fans,
                         bool panic, string status, bool noFans = false)
    {
        Updated?.Invoke(this, new FanReadings
        {
            CpuTemp = cpu,
            GpuTemp = gpu,
            SourceTemp = source,
            OutputPercent = _currentPercent,
            Mode = _settings.Mode,
            Engaged = _engaged,
            Panic = panic,
            NoControllableFans = noFans,
            SentinelLost = _sentinelLost,
            Status = _sentinelLost ? status + "   ·   WATCHDOG GONE - restart the app" : status,
            Fans = _hw.Fans,
        });
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
        if (_disposed) return;
        _disposed = true;

        try { _timer.Stop(); _timer.Dispose(); } catch { /* shutting down */ }

        // Before anything else - if this throws or the release hangs, the numbers
        // from the session are still on disk.
        try { LogSessionSummary(); } catch { /* never block shutdown */ }

        SafeRelease();

        // Give the watchdog (or the Super I/O) a moment to actually put the fans
        // back before this process - and its driver handle - goes away.
        Thread.Sleep(400);

        _hw.Dispose();
        DebugLog.Write("Controller disposed.");
    }
}
