// Session peaks, Peak Info report, and the peak hold the fans drive from.
using System.Timers;
using FanControlApp.Infrastructure;
using Timer = System.Timers.Timer;

namespace FanControlApp.Cooling;

public sealed partial class FanController
{
    // ---- peaks --------------------------------------------------------------

    /// <summary>Clear the displayed session peaks in every view.</summary>
    public void ResetDisplayPeaks()
    {
        _dispPeakCpu = float.NaN;
        _dispPeakGpu = float.NaN;
        _dispPeakCpuLoad = float.NaN;
        _dispPeakGpuLoad = float.NaN;

        // Peak Info times and names reset too.
        _dispPeakCpuAt = _dispPeakGpuAt = _dispPeakCpuLoadAt = _dispPeakGpuLoadAt = default;
        _dispPeakCpuFrom = _dispPeakCpuLoadFrom = _dispPeakGpuFrom = _dispPeakGpuLoadFrom = null;
    }

    /// <summary>The Peak Info button's four lines, built here so the window stays display-only.</summary>
    public string BuildPeakReport()
    {
        string gpuLoadLabel = GpuLoadIsTrue ? "Highest GPU load" : "Highest GPU busy time";
        return Line("Highest CPU temp", _dispPeakCpu, "°C", _dispPeakCpuAt, _dispPeakCpuFrom) + "\n"
             + Line("Highest CPU load", _dispPeakCpuLoad, "%", _dispPeakCpuLoadAt, _dispPeakCpuLoadFrom) + "\n\n"
             + Line("Highest GPU temp", _dispPeakGpu, "°C", _dispPeakGpuAt, _dispPeakGpuFrom) + "\n"
             + Line(gpuLoadLabel, _dispPeakGpuLoad, "%", _dispPeakGpuLoadAt, _dispPeakGpuLoadFrom);

        static string Line(string label, float v, string unit, DateTime at, string? from)
        {
            if (float.IsNaN(v)) return $"{label}: nothing recorded yet.";
            string s = $"{label}: {v:F0}{unit}  ·  {at:M/d h:mm tt}";
            if (from != null) s += $"  ·  from: {from}";
            return s;
        }
    }

    // NaN-safe max fold that reports a raise, which stamps Peak Info.
    private static bool RaisedInto(ref float peak, float v)
    {
        if (float.IsNaN(v) || (!float.IsNaN(peak) && v <= peak)) return false;
        peak = v;
        return true;
    }

    // Returns the hottest reading of the last PeakHoldMs and the chip that reached it.
    private (float Temp, string Src) HoldPeak(float temp, string src)
    {
        // A fresh engagement starts from the current temp, dropping peaks held from before.
        if (!_engaged)
        {
            _peakWindow.Clear();
            _prevWindowTemp = float.NaN;
        }

        // Only readings that survive 2 ticks are held, so a glitch can't rule a whole window.
        long nowMs = Environment.TickCount64;
        float paired = float.IsNaN(_prevWindowTemp) ? temp : MathF.Min(temp, _prevWindowTemp);
        _peakWindow.Enqueue((nowMs, paired, src));
        _prevWindowTemp = temp;

        while (_peakWindow.Peek().Ms < nowMs - PeakHoldMs) _peakWindow.Dequeue();

        var held = (Ms: nowMs, Temp: temp, Src: src);
        foreach (var e in _peakWindow) if (e.Temp > held.Temp) held = e;
        return (held.Temp, held.Src);
    }

    // NaN-safe max fold used by every peak.
    private static void MaxInto(ref float peak, float v) => RaisedInto(ref peak, v);

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
}
