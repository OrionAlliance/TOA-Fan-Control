// GPU max watts and the CPU and GPU load capture that feeds the load peaks.
using System.Timers;
using FanControlApp.Infrastructure;
using Timer = System.Timers.Timer;

namespace FanControlApp.Cooling;

public sealed partial class FanController
{
    // User max stands while the same card is installed; call under _gate.
    private int? UserGpuOverrideFor() =>
        _settings.GpuUserMaxWattsFor == _hw.GpuName ? _settings.GpuUserMaxWatts : null;

    /// <summary>Same rule, for the notice flow's branch logic.</summary>
    public bool GpuUserOverrideActive { get { lock (_gate) return UserGpuOverrideFor() != null; } }

    /// <summary>Apply a just-entered user max immediately.</summary>
    public void SetGpuMaxWattsOverride(int watts)
    {
        if (_hw.GpuMaxWatts != watts) ResetGpuLoadStream();
        _hw.GpuMaxWatts = watts;
        DebugLog.Write($"GPU max watts: user-set {watts}W for '{_hw.GpuName}' (live).");
    }

    /// <summary>Re-match the card after a library fetch so new or changed rows apply without a restart.</summary>
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

    /// <summary>True when GPU load is watts vs max; keyed on sensor presence so the label can't flicker.</summary>
    public bool GpuLoadIsTrue => _hw.GpuMaxWatts != null && _hw.GpuHasPowerSensor;

    /// <summary>The card's sensor-reported name.</summary>
    public string? GpuName => _hw.GpuName;

    /// <summary>Whether the card has a power sensor, so the popup never asks for unusable max watts.</summary>
    public bool GpuHasPowerSensor => _hw.GpuHasPowerSensor;

    // Sustained load = min of the last two ~1s captures, on its own cadence so conflict ticks can't shrink it.
    private void CaptureLoads()
    {
        if (Environment.TickCount64 - _lastLoadCaptureMs < 900) return;
        _lastLoadCaptureMs = Environment.TickCount64;

        // CPU load is busy time; no readable CPU power ceiling exists.
        float curCl = _hw.CpuLoad ?? float.NaN;

        // GPU: watts vs reference max when possible, else busy time; a missing reading poisons the pair.
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

    // The GPU load measure changed, so old samples must not survive as peaks.
    private void ResetGpuLoadStream()
    {
        _prevGpuLoad = float.NaN;
        _susGpuLoad = float.NaN;
        _peakGpuLoad = float.NaN;
        _dispPeakGpuLoad = float.NaN;
        _dispPeakGpuLoadAt = default;
        _dispPeakGpuLoadFrom = null;
    }
}
