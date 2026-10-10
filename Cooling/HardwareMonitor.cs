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

/// <summary>Thin LibreHardwareMonitor wrapper: opens, polls, and exposes this PC's temps and fan channels.</summary>
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

    /// <summary>Max of the D3D engine counters, falling back (logged once) to the idle-inflated GPU Core load.</summary>
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
                // Windows' busy-time counters can overshoot 100% on bursty engines; clamp and log once.
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

    /// <summary>The card's sensor-reported model name, used as the GPU library key.</summary>
    public string? GpuName => _gpuTemp?.Hardware.Name;

    /// <summary>Live GPU board power draw in watts, or null if the card has no power sensor.</summary>
    public float? GpuPowerW => _gpuPower?.Value;

    /// <summary>True when the card has a power sensor; without one true load is impossible.</summary>
    public bool GpuHasPowerSensor => _gpuPower != null;

    /// <summary>Card max watts for true load (null = unknown); a plain int backs it so cross-thread reads can't tear.</summary>
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

        // Disable LHM's per-sensor history; only live values are used and it leaks ~15-25 MB a day.
        foreach (IHardware h in Flatten(_computer.Hardware))
            foreach (ISensor s in h.Sensors)
                s.ValuesTimeWindow = TimeSpan.Zero;

        // Log hardware models for bug reports; they identify nothing personal, unlike paths or serials.
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
        catch { /* SMBIOS can be unreadable; never block startup for a log line */ }

        DebugLog.Write($"Hardware: board='{board?.Name ?? "-"}' bios='{bios}' chip='{chip}' cpu='{cpu}' gpu='{gpu}'");
        DebugLog.Write($"Hardware opened. cpuTemp='{CpuTempName}' gpuTemp='{GpuTempName}' boardTemp='{BoardTempName}' " +
                       $"gpuEngines={_gpuEngineLoads.Length} " +
                       $"fans=[{string.Join(", ", Fans.Select(f => $"{f.Name}{(f.CanControl ? "*" : "")}"))}]");

        // Resolve max watts fresh each start so a swapped card re-matches; skipped in the watchdog.
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

        // Tctl/Tdie reflects the die; the Super I/O "CPU" temp reads low and lags.
        _cpuTemp = all.FirstOrDefault(s => s.SensorType == SensorType.Temperature
                                           && s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(s => s.SensorType == SensorType.Temperature
                                              && s.Hardware.HardwareType == HardwareType.Cpu);

        // Core, not Hot Spot: Hot Spot runs ~15C hotter and would peg fans tuned around CPU temps.
        ISensor[] gpuTemps = all.Where(s => s.SensorType == SensorType.Temperature
                                            && IsGpu(s)).ToArray();

        _gpuTemp = gpuTemps.FirstOrDefault(s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                   ?? gpuTemps.FirstOrDefault(s => s.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
                   ?? gpuTemps.FirstOrDefault();

        // Scope GPU sensors to the temp's chip so iGPU and dGPU readings never mix.
        bool SameGpu(ISensor s) => _gpuTemp == null || s.Hardware == _gpuTemp.Hardware;

        // Board temp as a case-ambient proxy: named sensors first, then any plausible one.
        ISensor[] boardTemps = all.Where(s => s.SensorType == SensorType.Temperature
                                              && s.Hardware.HardwareType is HardwareType.Motherboard
                                                  or HardwareType.SuperIO).ToArray();
        _boardTemp = boardTemps.FirstOrDefault(s => s.Name.Contains("System", StringComparison.OrdinalIgnoreCase))
                     ?? boardTemps.FirstOrDefault(s => s.Name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase))
                     ?? boardTemps.FirstOrDefault(s => s.Value is > 5 and < 80);

        // CPU load feeds the peak-load marker directly.
        _cpuLoad = all.FirstOrDefault(s => s.SensorType == SensorType.Load
                                           && s.Hardware.HardwareType == HardwareType.Cpu
                                           && s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(s => s.SensorType == SensorType.Load
                                              && s.Hardware.HardwareType == HardwareType.Cpu);

        // Fallback only: this clock-relative load reads 50%+ at idle, never show it directly.
        _gpuLoad = all.FirstOrDefault(s => s.SensorType == SensorType.Load
                                           && SameGpu(s)
                                           && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                                           && IsGpu(s))
                   ?? all.FirstOrDefault(s => s.SensorType == SensorType.Load
                                              && SameGpu(s)
                                              && IsGpu(s));

        // D3D 3D and compute/Cuda engines only; video decode/copy blocks peg high on almost no power.
        _gpuEngineLoads = all.Where(s => s.SensorType == SensorType.Load
                                         && SameGpu(s)
                                         && IsGpu(s)
                                         && s.Name.StartsWith("D3D", StringComparison.OrdinalIgnoreCase)
                                         && (s.Name.EndsWith("3D", StringComparison.OrdinalIgnoreCase)
                                             || s.Name.Contains("Compute", StringComparison.OrdinalIgnoreCase)
                                             || s.Name.Contains("Cuda", StringComparison.OrdinalIgnoreCase))).ToArray();

        // Core clock tells real effort from idle, when a near-zero clock inflates "load".
        _gpuClock = all.FirstOrDefault(s => s.SensorType == SensorType.Clock
                                            && SameGpu(s)
                                            && IsGpu(s)
                                            && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase));

        // GPU power draw for true load, preferring whole-board readings over per-rail ones.
        ISensor[] gpuPowers = all.Where(s => s.SensorType == SensorType.Power
                                             && SameGpu(s)
                                             && IsGpu(s)).ToArray();
        _gpuPower = gpuPowers.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                    ?? gpuPowers.FirstOrDefault(s => s.Name.Contains("Board", StringComparison.OrdinalIgnoreCase))
                    ?? gpuPowers.FirstOrDefault();

        // Pair each Control sensor with the same-named Fan sensor, as the Nuvoton driver names them.
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

        // Add RPM-only headers too, with no control channel.
        foreach (ISensor r in rpms.Where(r => Fans.All(f => f.Name != r.Name)))
            Fans.Add(new FanChannel { Name = r.Name, RpmSensor = r });

        // Context fans for the log, never driven.
        GpuFan = Fans.FirstOrDefault(f => f.Name.Contains("gpu", StringComparison.OrdinalIgnoreCase));
        CpuFan = Fans.FirstOrDefault(f => f.Name.Equals("CPU Fan", StringComparison.OrdinalIgnoreCase))
                 ?? Fans.FirstOrDefault(f => f.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase));
    }

    public FanChannel? GpuFan { get; private set; }
    public FanChannel? CpuFan { get; private set; }

    private static bool IsGpu(ISensor s) =>
        s.Hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;

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
