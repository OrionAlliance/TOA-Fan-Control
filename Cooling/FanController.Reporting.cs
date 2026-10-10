// Publishing readings to the UI and writing sample and session log lines.
using System.Timers;
using FanControlApp.Infrastructure;
using Timer = System.Timers.Timer;

namespace FanControlApp.Cooling;

public sealed partial class FanController
{
    private void LogSample(float? cpu, float? gpu, float? hotter, List<FanChannel> controlled,
                           bool bios = false, float drivingTemp = float.NaN)
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
        // Show the held peak when it explains fans running above the current temp.
        string hold = drivingTemp > (hotter ?? float.MinValue) + 0.5f ? $" hold={drivingTemp:F1}" : "";
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

    private void Publish(float? cpu, float? gpu, float? source, string status,
                         bool noFans = false, bool biosHasFans = false)
    {
        // Display peaks (separate from the log's whole-session peaks); a raise stamps Peak Info when and who.
        DateTime stamp = DateTime.Now;
        if (RaisedInto(ref _dispPeakCpu, cpu ?? float.NaN))
        {
            _dispPeakCpuAt = stamp;
            PeakCulprit.Identify(n => _dispPeakCpuFrom = n);
        }
        if (RaisedInto(ref _dispPeakGpu, gpu ?? float.NaN))
        {
            _dispPeakGpuAt = stamp;
            PeakCulprit.IdentifyGpu(n => _dispPeakGpuFrom = n);
        }
        if (RaisedInto(ref _dispPeakCpuLoad, _susCpuLoad))
        {
            _dispPeakCpuLoadAt = stamp;
            PeakCulprit.Identify(n => _dispPeakCpuLoadFrom = n);
        }
        if (RaisedInto(ref _dispPeakGpuLoad, _susGpuLoad))
        {
            _dispPeakGpuLoadAt = stamp;
            PeakCulprit.IdentifyGpu(n => _dispPeakGpuLoadFrom = n);
        }

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
            BiosHasFans = biosHasFans,
            SentinelLost = _sentinelLost,
            Conflict = _conflict,
            Status = _sentinelLost ? status + "   ·   WATCHDOG GONE - restart the app" : status,
            Fans = _hw.Fans,
            DrivenFans = ControlledFanNames,
        };

        // A subscriber throw must not unwind into Poll and cost fan control.
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
}
