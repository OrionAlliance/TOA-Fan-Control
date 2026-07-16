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

    public string Status { get; init; } = "";
    public IReadOnlyList<FanChannel> Fans { get; init; } = Array.Empty<FanChannel>();
}

/// <summary>
/// Polls temps once a second and drives the controlled fan headers.
///
/// The invariant that matters: this class must never leave a fan header seized
/// by software when it can't see what the temperature is. Any doubt - sensor
/// read fails, an exception escapes, the app shuts down - and the headers go
/// straight back to the BIOS curve, which is always a safe fallback.
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

    private readonly object _gate = new();
    private readonly HardwareMonitor _hw = new();
    private readonly Timer _timer = new(TickMs);

    private FanSettings _settings;
    private List<FanChannel> _controlled = new();
    private float _currentPercent = 50f;
    private bool _engaged;
    private int _blindTicks;
    private bool _disposed;

    public event EventHandler<FanReadings>? Updated;

    public FanSettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    public HardwareMonitor Hardware => _hw;

    public FanController(FanSettings settings)
    {
        _settings = settings;
        _timer.Elapsed += OnTick;
        _timer.AutoReset = true;
    }

    public void Start()
    {
        _hw.Open();
        ResolveControlledFans();
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

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        try
        {
            Poll();
        }
        catch (Exception ex)
        {
            // An exception here means we can no longer trust our own readings.
            DebugLog.Write("Tick failed - releasing fans to BIOS.", ex);
            SafeRelease();
        }
    }

    private void Poll()
    {
        _hw.Refresh();

        FanSettings s;
        List<FanChannel> controlled;
        lock (_gate)
        {
            s = _settings;
            controlled = _controlled.ToList();
        }

        float? cpu = _hw.CpuTemp;
        float? gpu = _hw.GpuTemp;
        float? source = s.Source switch
        {
            TempSource.Cpu => cpu,
            TempSource.Gpu => gpu,
            _ => Max(cpu, gpu),
        };

        // If nothing resolved, the app is a thermometer with no hands. Say so
        // loudly rather than looking busy while controlling nothing.
        if (controlled.Count == 0)
        {
            Publish(cpu, gpu, source, controlled, panic: false, noFans: true,
                    status: "No controllable fans found - the BIOS is running your fans. See fan_debug.log.");
            return;
        }

        // No temperature means no basis for a decision. Hand back to the BIOS.
        if (source is not { } temp || float.IsNaN(temp))
        {
            _blindTicks++;
            if (_blindTicks >= MaxBlindTicks && _engaged)
            {
                DebugLog.Write($"No temperature for {_blindTicks} ticks - releasing to BIOS.");
                SafeRelease();
            }

            Publish(cpu, gpu, null, controlled, panic: false,
                    status: "No temperature reading - BIOS has the fans.");
            return;
        }

        _blindTicks = 0;

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

        if (controlled.Count > 0) _engaged = true;

        Publish(cpu, gpu, temp, controlled, panic, status);
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
            Status = status,
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
    /// Give every seized header back to the BIOS. Safe to call repeatedly, from
    /// any thread, and during shutdown - it must never throw.
    /// </summary>
    public void SafeRelease()
    {
        List<FanChannel> controlled;
        lock (_gate) controlled = _controlled.ToList();

        foreach (FanChannel f in controlled)
        {
            try
            {
                f.Release();
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Release failed for '{f.Name}'.", ex);
            }
        }

        if (_engaged) DebugLog.Write("Fans released to BIOS.");
        _engaged = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _timer.Stop(); _timer.Dispose(); } catch { /* shutting down */ }

        SafeRelease();

        // Give the Super I/O a moment to latch the default mode before the
        // driver unloads underneath it.
        Thread.Sleep(150);

        _hw.Dispose();
        DebugLog.Write("Controller disposed.");
    }
}
