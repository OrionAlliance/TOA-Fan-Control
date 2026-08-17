using FanControlApp.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace FanControlApp.Cooling;

/// <summary>A single fan header: its RPM reading and (if writable) its PWM control.</summary>
public sealed class FanChannel
{
    public required string Name { get; init; }
    public ISensor? RpmSensor { get; init; }
    public ISensor? ControlSensor { get; init; }

    public float? Rpm => RpmSensor?.Value;
    public float? Percent => ControlSensor?.Value;
    public bool CanControl => ControlSensor?.Control != null;

    public void SetPercent(float percent)
    {
        IControl? c = ControlSensor?.Control;
        if (c == null) return;
        c.SetSoftware(Math.Clamp(percent, c.MinSoftwareValue, c.MaxSoftwareValue));
    }

    /// <summary>Hand this header back to the BIOS fan curve.</summary>
    public void Release() => ControlSensor?.Control?.SetDefault();
}

internal sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);

    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware sub in hardware.SubHardware)
            sub.Accept(this);
    }

    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}

/// <summary>
/// Thin wrapper over LibreHardwareMonitor. Knows how to open the hardware, poll
/// it, and hand back the temps and fan channels this machine actually exposes.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _opened;

    private ISensor? _cpuTemp;
    private ISensor? _gpuTemp;
    private ISensor? _cpuLoad;
    private ISensor? _gpuLoad;
    private ISensor[] _gpuEngineLoads = Array.Empty<ISensor>();
    private ISensor? _gpuClock;
    private ISensor? _gpuPower;
    private ISensor? _boardTemp;

    public List<FanChannel> Fans { get; } = new();

    public float? CpuTemp => _cpuTemp?.Value;
    public float? GpuTemp => _gpuTemp?.Value;
    public float? CpuLoad => _cpuLoad?.Value;
    public float? BoardTemp => _boardTemp?.Value;
    public string BoardTempName => _boardTemp?.Name ?? "-";
    public float? GpuCoreClockMhz => _gpuClock?.Value;

    /// <summary>True GPU utilization: max across the Windows D3D engine counters
    /// (clock-independent, matches Task Manager). Falls back to the clock-relative
    /// GPU Core load when no engine counter has a value - logged once, because the
    /// fallback number is idle-inflated and the log must say which one it shows.</summary>
    public float? GpuEngineLoad
    {
        get
        {
            float max = float.NaN;
            ISensor? top = null;
            foreach (ISensor s in _gpuEngineLoads)
                if (s.Value is { } v && (float.IsNaN(max) || v > max)) { max = v; top = s; }
            if (!float.IsNaN(max))
            {
                // Windows' busy-time accounting can overshoot on bursty engines
                // (field-seen: a CUDA node at 123% during a media-server nightly
                // job). Over 100% is impossible - clamp, and name the engine once
                // so the log can explain the reading.
                if (max > 100f)
                {
                    if (!_engineOverflowLogged)
                    {
                        _engineOverflowLogged = true;
                        DebugLog.Write($"GPU engine '{top!.Name}' reported {max:F0}% - counter overshoot, clamped to 100.");
                    }
                    max = 100f;
                }
                return max;
            }

            if (!_engineFallbackLogged && _gpuLoad != null)
            {
                _engineFallbackLogged = true;
                DebugLog.Write("GPU engine counters unavailable - load falls back to the clock-relative GPU Core sensor.");
            }
            return _gpuLoad?.Value;
        }
    }

    private bool _engineFallbackLogged;
    private bool _engineOverflowLogged;

    public string CpuTempName => _cpuTemp?.Name ?? "-";
    public string GpuTempName => _gpuTemp?.Name ?? "-";

    /// <summary>The card's sensor-reported model name - the GPU library's lookup key.</summary>
    public string? GpuName => _gpuTemp?.Hardware.Name;

    /// <summary>Live GPU board power draw in watts, or null if the card has no power sensor.</summary>
    public float? GpuPowerW => _gpuPower?.Value;

    /// <summary>True when the card exposes a power sensor at all - without one,
    /// true load is impossible and a stored max watts can never be used.</summary>
    public bool GpuHasPowerSensor => _gpuPower != null;

    /// <summary>This card's max watts - the true-load gauge's denominator. Set from
    /// the GPU library at open; a user-entered value (unlisted card) overwrites it.
    /// Null = unknown, markers fall back to busy time. Backed by a plain int
    /// (0 = unknown) so the timer thread can never tear a cross-thread read.</summary>
    public int? GpuMaxWatts
    {
        get { int v = _gpuMaxWatts; return v == 0 ? null : v; }
        set => _gpuMaxWatts = value ?? 0;
    }
    private int _gpuMaxWatts;

    public HardwareMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
        };
    }

    public void Open()
    {
        if (_opened) return;
        _computer.Open();
        _opened = true;
        Refresh();
        Discover();

        // LHM appends every reading to a per-sensor history list (up to a day's
        // worth) on each update. We only ever use the live value, so that's
        // ~15-25 MB/day of diary nobody reads - measured as the app's slow
        // memory creep across two soak tests. Zero window = keep nothing.
        foreach (IHardware h in Flatten(_computer.Hardware))
            foreach (ISensor s in h.Sensors)
                s.ValuesTimeWindow = TimeSpan.Zero;

        // Hardware models matter for bug reports (fan control lives on the board's
        // Super I/O chip, and its behaviour can shift with a BIOS update) and
        // identify nothing about the person - unlike paths or serial numbers.
        IHardware? board = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
        string chip = board?.SubHardware.FirstOrDefault(s => s.HardwareType == HardwareType.SuperIO)?.Name ?? "-";
        string cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu)?.Name ?? "-";
        string gpu = _gpuTemp?.Hardware.Name ?? "-";

        string bios = "-";
        try
        {
            LibreHardwareMonitor.Hardware.BiosInformation? b = _computer.SMBios?.Bios;
            if (b != null) bios = $"{b.Vendor} {b.Version}".Trim();
        }
        catch { /* SMBIOS can be unreadable on odd systems - never block startup for a log line */ }

        DebugLog.Write($"Hardware: board='{board?.Name ?? "-"}' bios='{bios}' chip='{chip}' cpu='{cpu}' gpu='{gpu}'");
        DebugLog.Write($"Hardware opened. cpuTemp='{CpuTempName}' gpuTemp='{GpuTempName}' boardTemp='{BoardTempName}' " +
                       $"gpuEngines={_gpuEngineLoads.Length} " +
                       $"fans=[{string.Join(", ", Fans.Select(f => $"{f.Name}{(f.CanControl ? "*" : "")}"))}]");

        // The true-load gauge's denominator, resolved fresh every startup - so a
        // swapped card re-matches on its own with no stale carry-over. Silent in
        // the watchdog process, which opens hardware but never loads the library.
        if (GpuLibrary.IsLoaded)
        {
            GpuMaxWatts = GpuLibrary.MaxWattsFor(gpu);
            DebugLog.Write(GpuMaxWatts is { } w
                ? $"GPU library match: '{gpu}' = {w}W reference max (power sensor: {_gpuPower?.Name ?? "NONE"})."
                : $"GPU library: no entry for '{gpu}' - true-load falls back to busy time.");
        }
    }

    public void Refresh()
    {
        if (!_opened) return;
        _computer.Accept(_visitor);
    }

    private void Discover()
    {
        ISensor[] all = Flatten(_computer.Hardware).SelectMany(h => h.Sensors).ToArray();

        // Tctl/Tdie is the sensor that actually reflects the die; the Super I/O's
        // "CPU" temp reads several degrees low and lags badly.
        _cpuTemp = all.FirstOrDefault(s => s.SensorType == SensorType.Temperature
                                           && s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(s => s.SensorType == SensorType.Temperature
                                              && s.Hardware.HardwareType == HardwareType.Cpu);

        // Core, not Hot Spot. Hot Spot is the more alarming number and it's what
        // throttles the card, but it runs 90-100C under a normal load - roughly
        // 15C above Core. Feeding that into a controller tuned around CPU
        // temperatures would peg the fans permanently. Core is the comparable one.
        ISensor[] gpuTemps = all.Where(s => s.SensorType == SensorType.Temperature
                                            && s.Hardware.HardwareType is HardwareType.GpuAmd
                                                or HardwareType.GpuNvidia
                                                or HardwareType.GpuIntel).ToArray();

        _gpuTemp = gpuTemps.FirstOrDefault(s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                   ?? gpuTemps.FirstOrDefault(s => s.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
                   ?? gpuTemps.FirstOrDefault();

        // Every other GPU sensor is scoped to the SAME chip as the temp - on an
        // iGPU+dGPU machine, mixing chips would pair one GPU's load or clock with
        // the other GPU's temperature.
        bool SameGpu(ISensor s) => _gpuTemp == null || s.Hardware == _gpuTemp.Hardware;

        // Case-ambient proxy: the board's own temp - the weather every other
        // reading happens in. Named sensors first; any plausible one as fallback.
        ISensor[] boardTemps = all.Where(s => s.SensorType == SensorType.Temperature
                                              && s.Hardware.HardwareType is HardwareType.Motherboard
                                                  or HardwareType.SuperIO).ToArray();
        _boardTemp = boardTemps.FirstOrDefault(s => s.Name.Contains("System", StringComparison.OrdinalIgnoreCase))
                     ?? boardTemps.FirstOrDefault(s => s.Name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase))
                     ?? boardTemps.FirstOrDefault(s => s.Value is > 5 and < 80);

        // CPU load feeds the peak-load marker directly (time-based, honest as-is).
        _cpuLoad = all.FirstOrDefault(s => s.SensorType == SensorType.Load
                                           && s.Hardware.HardwareType == HardwareType.Cpu
                                           && s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(s => s.SensorType == SensorType.Load
                                              && s.Hardware.HardwareType == HardwareType.Cpu);

        // Kept ONLY as GpuEngineLoad's fallback - this sensor is "% busy at the
        // CURRENT clock", which reads 50%+ at idle. Never surface it directly.
        _gpuLoad = all.FirstOrDefault(s => s.SensorType == SensorType.Load
                                           && SameGpu(s)
                                           && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                                           && s.Hardware.HardwareType is HardwareType.GpuAmd
                                               or HardwareType.GpuNvidia
                                               or HardwareType.GpuIntel)
                   ?? all.FirstOrDefault(s => s.SensorType == SensorType.Load
                                              && SameGpu(s)
                                              && s.Hardware.HardwareType is HardwareType.GpuAmd
                                                  or HardwareType.GpuNvidia
                                                  or HardwareType.GpuIntel);

        // The Windows D3D engine counters (Task Manager's numbers): true
        // utilization, NOT clock-relative, honest at any clock, identical on every
        // GPU. Use ONLY the 3D + compute engines (NVIDIA names its compute node
        // "Cuda") - the real graphics/compute horsepower. Excludes the
        // fixed-function video-decode/copy blocks, which peg high during a video
        // but use almost no power (why TM over-reports).
        _gpuEngineLoads = all.Where(s => s.SensorType == SensorType.Load
                                         && SameGpu(s)
                                         && s.Hardware.HardwareType is HardwareType.GpuAmd
                                             or HardwareType.GpuNvidia
                                             or HardwareType.GpuIntel
                                         && s.Name.StartsWith("D3D", StringComparison.OrdinalIgnoreCase)
                                         && (s.Name.EndsWith("3D", StringComparison.OrdinalIgnoreCase)
                                             || s.Name.Contains("Compute", StringComparison.OrdinalIgnoreCase)
                                             || s.Name.Contains("Cuda", StringComparison.OrdinalIgnoreCase))).ToArray();

        // Core clock, to tell real effort from the idle-clock quirk: a sleeping
        // GPU reports 50%+ "load" for desktop crumbs because the clock is near zero.
        _gpuClock = all.FirstOrDefault(s => s.SensorType == SensorType.Clock
                                            && SameGpu(s)
                                            && s.Hardware.HardwareType is HardwareType.GpuAmd
                                                or HardwareType.GpuNvidia
                                                or HardwareType.GpuIntel
                                            && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase));

        // Board power draw - the true-load gauge's live numerator. Prefer the
        // whole-board reading ("GPU Package"/"GPU Power") over per-rail ones.
        ISensor[] gpuPowers = all.Where(s => s.SensorType == SensorType.Power
                                             && SameGpu(s)
                                             && s.Hardware.HardwareType is HardwareType.GpuAmd
                                                 or HardwareType.GpuNvidia
                                                 or HardwareType.GpuIntel).ToArray();
        _gpuPower = gpuPowers.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                    ?? gpuPowers.FirstOrDefault(s => s.Name.Contains("Board", StringComparison.OrdinalIgnoreCase))
                    ?? gpuPowers.FirstOrDefault();

        // Pair each Control sensor with the Fan sensor of the same name - that's
        // how the Nuvoton driver names them (e.g. "Chassis Fan #2" appears as both).
        ISensor[] controls = all.Where(s => s.SensorType == SensorType.Control).ToArray();
        ISensor[] rpms = all.Where(s => s.SensorType == SensorType.Fan).ToArray();

        Fans.Clear();
        foreach (ISensor c in controls)
        {
            Fans.Add(new FanChannel
            {
                Name = c.Name,
                ControlSensor = c,
                RpmSensor = rpms.FirstOrDefault(r => r.Name == c.Name),
            });
        }

        // Headers with no fan plugged in still report a control channel; keep them
        // (so the UI can show them greyed) but they'll read 0 RPM forever.
        foreach (ISensor r in rpms.Where(r => Fans.All(f => f.Name != r.Name)))
            Fans.Add(new FanChannel { Name = r.Name, RpmSensor = r });

        // Context fans for the log - never driven, but their effort tells the thermal story.
        GpuFan = Fans.FirstOrDefault(f => f.Name.Contains("gpu", StringComparison.OrdinalIgnoreCase));
        CpuFan = Fans.FirstOrDefault(f => f.Name.Equals("CPU Fan", StringComparison.OrdinalIgnoreCase))
                 ?? Fans.FirstOrDefault(f => f.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase));
    }

    public FanChannel? GpuFan { get; private set; }
    public FanChannel? CpuFan { get; private set; }

    public FanChannel? FindFan(string name) =>
        Fans.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> hardware)
    {
        foreach (IHardware h in hardware)
        {
            yield return h;
            foreach (IHardware sub in Flatten(h.SubHardware))
                yield return sub;
        }
    }

    public void Dispose()
    {
        if (!_opened) return;
        try { _computer.Close(); }
        catch (Exception ex) { DebugLog.Write("Computer.Close failed", ex); }
        _opened = false;
    }
}
