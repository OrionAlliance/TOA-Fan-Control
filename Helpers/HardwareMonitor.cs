using LibreHardwareMonitor.Hardware;

namespace FanControlApp.Helpers;

/// <summary>A single fan header: its RPM reading and (if writable) its PWM control.</summary>
public sealed class FanChannel
{
    public required string Name { get; init; }
    public ISensor? RpmSensor { get; init; }
    public ISensor? ControlSensor { get; init; }

    public float? Rpm => RpmSensor?.Value;
    public float? Percent => ControlSensor?.Value;
    public bool CanControl => ControlSensor?.Control != null;

    /// <summary>True once we've seized this channel from the BIOS.</summary>
    public bool IsSoftwareControlled =>
        ControlSensor?.Control?.ControlMode == ControlMode.Software;

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

    public List<FanChannel> Fans { get; } = new();

    public float? CpuTemp => _cpuTemp?.Value;
    public float? GpuTemp => _gpuTemp?.Value;

    public string CpuTempName => _cpuTemp?.Name ?? "-";
    public string GpuTempName => _gpuTemp?.Name ?? "-";

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

        DebugLog.Write($"Hardware opened. cpuTemp='{CpuTempName}' gpuTemp='{GpuTempName}' " +
                       $"fans=[{string.Join(", ", Fans.Select(f => $"{f.Name}{(f.CanControl ? "*" : "")}"))}]");
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
    }

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
