using LibreHardwareMonitor.Hardware;

// Step-0 probe for the true-load feature: does THIS machine expose CPU/GPU
// power (watts) sensors through the same LHM version the app ships? Writes
// findings next to the exe so an elevated run can still be read afterwards.

var lines = new List<string>();
void Log(string s) { lines.Add(s); Console.WriteLine(s); }

var computer = new Computer
{
    IsCpuEnabled = true,
    IsGpuEnabled = true,
};
computer.Open();

// Two refreshes a second apart - some sensors need a delta to produce a value.
foreach (IHardware hw in computer.Hardware) hw.Update();
Thread.Sleep(1000);
foreach (IHardware hw in computer.Hardware) hw.Update();

foreach (IHardware hw in computer.Hardware)
{
    Log($"HW: [{hw.HardwareType}] '{hw.Name}'");
    foreach (ISensor s in hw.Sensors.OrderBy(s => s.SensorType.ToString()))
    {
        if (s.SensorType is SensorType.Power or SensorType.Clock or SensorType.Load)
            Log($"   {s.SensorType,-6} '{s.Name}' = {s.Value?.ToString("F1") ?? "null"}");
    }
}

computer.Close();
File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "probe_output.txt"), lines);
Console.WriteLine("\nwritten to probe_output.txt");
