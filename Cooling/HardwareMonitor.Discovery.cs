// Finds the temp, load, clock, power and fan sensors on this PC.
using FanControlApp.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace FanControlApp.Cooling;

public sealed partial class HardwareMonitor
{
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
}
