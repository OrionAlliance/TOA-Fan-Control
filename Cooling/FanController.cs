using System.Timers;
using FanControlApp.Infrastructure;
using Timer = System.Timers.Timer;

namespace FanControlApp.Cooling;

/// <summary>A snapshot of one poll, handed to the UI to display.</summary>
public sealed class FanReadings
{
    public float? CpuTemp { get; init; }
    public float? GpuTemp { get; init; }

    /// <summary>The held peak the fans match while driving, else the current hotter of CPU/GPU (null if none).</summary>
    public float? SourceTemp { get; init; }
    public float OutputPercent { get; init; }

    /// <summary>Session peaks since start or Reset peaks; every view renders these so they always agree.</summary>
    public float PeakCpu { get; init; } = float.NaN;
    public float PeakGpu { get; init; } = float.NaN;

    /// <summary>Session peak load %, tracked independently of peak temp.</summary>
    public float PeakCpuLoad { get; init; } = float.NaN;
    public float PeakGpuLoad { get; init; } = float.NaN;

    /// <summary>Live load % from the latest ~1s capture.</summary>
    public float CpuLoad { get; init; } = float.NaN;
    public float GpuLoad { get; init; } = float.NaN;

    /// <summary>True = GPU load is watts vs card max, false = busy-time fallback; drives the tooltip wording.</summary>
    public bool GpuLoadIsTrue { get; init; }

    /// <summary>Nothing to drive, so the app is only a thermometer.</summary>
    public bool NoControllableFans { get; init; }

    /// <summary>The BIOS curve owns the fans, so displays must not show a fan % the app isn't commanding.</summary>
    public bool BiosHasFans { get; init; }

    /// <summary>The watchdog died mid-session; a clean exit can no longer restore the BIOS curve.</summary>
    public bool SentinelLost { get; init; }

    /// <summary>Another program is writing fan speeds too; we're out-writing it.</summary>
    public bool Conflict { get; init; }

    public string Status { get; init; } = "";
    public IReadOnlyList<FanChannel> Fans { get; init; } = Array.Empty<FanChannel>();

    /// <summary>Names of the fans the app is driving, shown as dials.</summary>
    public IReadOnlyList<string> DrivenFans { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Every second, fan % = the hotter of CPU/GPU in C (leaning up to +5 past 70C), so hot always means fast.
/// Only the first grabber of a header holds the real BIOS settings, so the watchdog (when attached) owns the release.
/// </summary>
public sealed class FanController : IDisposable
{
    private const double TickMs = 1000;

    // During a conflict, re-assert every 250ms; the Super I/O has no concept of ownership.
    private const double ConflictTickMs = 250;

    // Hard 30% floor, deliberately not a setting: below it Chassis Fan #2 stalls to 0 RPM.
    public const float FloorPercent = 30f;
    public const float CeilingPercent = 100f;

    // Hot lean: past 70C fans run up to +5 ahead of the temp, faded in by 75C so the target never steps at a boundary.
    private const float HotLeanFromC = 70f;
    private const float HotLeanMax = 5f;

    // Ramp up fast, down slow (fast down-ramps audibly pulse); per second so conflict ticks don't speed ramps.
    private const float SlewUpPerSec = 8f;
    private const float SlewDownPerSec = 3f;

    // Fans track the window's hottest reading so bursty loads don't make them surf the sawtooth.
    private const long PeakHoldMs = 15_000;

    private const int MaxBlindTicks = 3;

    // Conflict = chip duty differs from our command; tolerance covers PWM quantization, N misses skips flukes.
    private const float ForeignWriteTolerance = 2.5f;
    private const int ForeignTicksToConfirm = 3;
    private const long ConflictClearMs = 30_000;

    // A SAMPLE log line every 5s, time-based since the tick rate varies.
    private const long SampleEveryMs = 5_000;

    private readonly object _gate = new();
    private readonly HardwareMonitor _hw = new();
    private readonly Timer _timer = new(TickMs);

    private FanSettings _settings;
    private WatchdogLink? _link;
    private List<FanChannel> _controlled = new();
    private List<FanChannel> _candidates = new();
    private float _currentPercent = FloorPercent;
    // Peak-hold state, owned by HoldPeak.
    private readonly Queue<(long Ms, float Temp, string Src)> _peakWindow = new();
    private float _prevWindowTemp = float.NaN;
    private bool _engaged;
    private bool _paused;
    private int _blindTicks;
    private int _disposedFlag; // Interlocked: UI thread and ProcessExit can both Dispose
    private bool _publishFaulted; // log-on-change gate for subscriber throws

    // Foreign-writer tracking, stamped with monotonic TickCount64 so clock jumps can't fake one.
    private int _foreignTicks;
    private bool _conflict;
    private long _lastForeignWriteMs;
    private long _lastSampleLogMs;

    // Session telemetry for the log, so it records what the app actually did.
    private int _tickCount;
    private bool _sentinelLost;
    private float _peakCpu = float.NaN;
    private float _peakGpu = float.NaN;
    private float _peakCpuLoad = float.NaN;
    private float _peakGpuLoad = float.NaN;
    private float _peakOut;
    private readonly Dictionary<string, float> _peakRpm = new();

    // The peaks the UI shows (cleared by Reset peaks; the log's stay whole-session).
    private float _dispPeakCpu = float.NaN;
    private float _dispPeakGpu = float.NaN;
    private float _dispPeakCpuLoad = float.NaN;
    private float _dispPeakGpuLoad = float.NaN;

    // Peak Info: when and by whom each peak was set; memory only, wiped by Reset peaks.
    private DateTime _dispPeakCpuAt, _dispPeakGpuAt, _dispPeakCpuLoadAt, _dispPeakGpuLoadAt;
    private string? _dispPeakCpuFrom, _dispPeakCpuLoadFrom, _dispPeakGpuFrom, _dispPeakGpuLoadFrom;

    // A load must hold 2 consecutive ~1s captures to count, so one-poll blips are ignored.
    private float _prevCpuLoad = float.NaN;
    private float _prevGpuLoad = float.NaN;
    private float _susCpuLoad = float.NaN;
    private float _susGpuLoad = float.NaN;
    private long _lastLoadCaptureMs;
    private readonly long _startedMs = Environment.TickCount64;

    public event EventHandler<FanReadings>? Updated;

    // Live instance: safe only while all mutations and multi-field reads stay on the dispatcher thread.
    public FanSettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    /// <summary>The headers we will write to, which the watchdog must guard.</summary>
    public IReadOnlyList<string> ControlledFanNames
    {
        get { lock (_gate) return _controlled.Select(f => f.Name).ToList(); }
    }

    /// <summary>Every fan the app could drive, with current RPM, for the fan picker.</summary>
    public IReadOnlyList<(string Name, float? Rpm)> CandidateFans
    {
        get { lock (_gate) return _candidates.Select(f => (f.Name, f.Rpm)).ToList(); }
    }

    public FanController(FanSettings settings)
    {
        _settings = settings;
        _timer.Elapsed += OnTick;
        _timer.AutoReset = true;
    }

    /// <summary>Open hardware and resolve fans without writing; the watchdog must take them first.</summary>
    public void OpenHardware()
    {
        _hw.Open();

        // A user-entered GPU max outranks the library, but only for the same card.
        lock (_gate)
        {
            if (UserGpuOverrideFor() is { } w)
            {
                _hw.GpuMaxWatts = w;
                DebugLog.Write($"GPU max watts: user-set {w}W for '{_hw.GpuName}'.");
            }
        }

        ResolveControlledFans();
    }

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
        _currentPercent = FloorPercent;
        _timer.Start();
        DebugLog.Write("Controller started.");
    }

    // Name substrings left on the BIOS: pump must run flat-out, cpu stays as a failsafe, chipset/gpu self-manage.
    private static readonly string[] SkipFanPatterns = { "pump", "cpu", "chipset", "gpu" };

    private void ResolveControlledFans()
    {
        lock (_gate)
        {
            _candidates = new List<FanChannel>();
            var skipped = new List<string>();

            foreach (FanChannel f in _hw.Fans)
            {
                if (!f.CanControl) continue; // read-only reading or an empty header

                string name = f.Name.ToLowerInvariant();
                if (SkipFanPatterns.Any(p => name.Contains(p)))
                {
                    skipped.Add(f.Name);
                    continue;
                }

                _candidates.Add(f);
            }

            // The picker only narrows the safe candidates; null = never picked, so drive them all.
            List<string>? picked = _settings.SelectedFans;
            _controlled = picked == null
                ? _candidates.ToList()
                : _candidates.Where(f =>
                        picked.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                    .ToList();

            var unchecked_ = _candidates.Except(_controlled).Select(f => f.Name).ToList();
            DebugLog.Write(
                $"Driving: [{string.Join(", ", _controlled.Select(f => f.Name))}]  " +
                $"(left on BIOS: [{string.Join(", ", skipped)}]" +
                (unchecked_.Count > 0 ? $", unchecked by user: [{string.Join(", ", unchecked_)}]" : "") +
                ")");
        }
    }

    /// <param name="reresolve">True only before the watchdog starts; it guards only the set it seized.</param>
    public void UpdateSettings(Action<FanSettings> mutate, bool reresolve = false)
    {
        lock (_gate)
        {
            mutate(_settings);
            SettingsStore.Save(_settings);
        }
        if (reresolve) ResolveControlledFans();
    }

    // ---- pause / resume -----------------------------------------------------

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

    // MaxInto that reports a raise, which stamps Peak Info.
    private static bool RaisedInto(ref float peak, float v)
    {
        if (float.IsNaN(v) || (!float.IsNaN(peak) && v <= peak)) return false;
        peak = v;
        return true;
    }

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
            // No watchdog: we grabbed first, so our saved defaults are the real BIOS ones.
            foreach (FanChannel f in controlled)
            {
                try { f.Release(); }
                catch (Exception ex) { DebugLog.Write($"Release failed for '{f.Name}'.", ex); }
            }
        }

        _engaged = false;
    }

    // ---- the loop -----------------------------------------------------------

    // 1 while inside Poll(); a stalled read skips ticks since two threads in the sensor library is undefined.
    private int _tickBusy;

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _tickBusy, 1, 0) != 0)
            return;

        try
        {
            // A queued tick can land after Dispose - it must never touch hardware.
            if (System.Threading.Volatile.Read(ref _disposedFlag) != 0) return;
            Poll();
        }
        catch (Exception ex)
        {
            // We can no longer trust our readings.
            DebugLog.Write("Tick failed - handing the fans back to the BIOS.", ex);
            SafeRelease();
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _tickBusy, 0);
        }
    }

    private void Poll()
    {
        _hw.Refresh();
        CaptureLoads(); // before ANY peak tracking, so log and display agree

        List<FanChannel> controlled;
        WatchdogLink? link;
        bool paused;
        lock (_gate)
        {
            controlled = _controlled.ToList();
            link = _link;
            paused = _paused;
        }

        float? cpu = _hw.CpuTemp;
        float? gpu = _hw.GpuTemp;

        // Whichever is hotter.
        float? source = Max(cpu, gpu);

        if (controlled.Count == 0)
        {
            Publish(cpu, gpu, source, noFans: true, biosHasFans: true,
                    status: "No controllable fans found - the BIOS is running your fans. See fan_debug.log.");
            return;
        }

        // A dead sentinel still looks fine (events outlive it), so fall back to owning the release ourselves.
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
            // Not writing, so no conflict to win.
            if (_conflict)
            {
                _conflict = false;
                _foreignTicks = 0;
                SetTickRate(TickMs);
                DebugLog.Write("Paused during a conflict - conflict state cleared.");
            }
            // Keep logging what the BIOS does.
            TrackPeaks(cpu, gpu, controlled);
            if (Environment.TickCount64 - _lastSampleLogMs >= SampleEveryMs)
            {
                _lastSampleLogMs = Environment.TickCount64;
                LogSample(cpu, gpu, source, controlled, bios: true);
            }

            Publish(cpu, gpu, source, biosHasFans: true,
                    status: "Paused - the BIOS curve has your fans.");
            return;
        }

        // No temperature, no decision: hand back to the BIOS.
        if (source is not { } temp || float.IsNaN(temp))
        {
            _blindTicks++;
            bool handedBack = _blindTicks >= MaxBlindTicks;
            // The watchdog holds the fans from startup, so a blind start must hand back too.
            bool held = _engaged || (link != null && link.Ready.WaitOne(0));
            if (handedBack && held)
            {
                DebugLog.Write($"No temperature for {_blindTicks} ticks - handing the fans back.");
                HandBackToBios();
            }

            Publish(cpu, gpu, null, biosHasFans: handedBack,
                    status: handedBack ? "No temperature reading - BIOS has the fans."
                                       : "No temperature reading - handing the fans to the BIOS...");
            return;
        }

        _blindTicks = 0;

        // Drive off the window's hottest reading; spikes land instantly, only quietening waits.
        string src = (gpu ?? float.MinValue) >= (cpu ?? float.MinValue) ? "GPU" : "CPU";
        (float driving, string drivingSrc) = HoldPeak(temp, src);

        // Never write before the watchdog holds the headers, or it has no BIOS state to restore.
        if (link != null && !link.Ready.WaitOne(0))
        {
            link.Resume.Set();
            Publish(cpu, gpu, temp,
                    status: "Waiting for the watchdog to take the fans...");
            return;
        }

        // This tick's interval, read before DetectForeignWriter can change it.
        float dt = (float)(_timer.Interval / 1000.0);

        // Check the chip still holds our last write; if another app (often RGB suites) wrote, out-write it.
        DetectForeignWriter(controlled);

        // fan % = temperature plus the hot lean, floored and capped.
        float lean = Math.Clamp(driving - HotLeanFromC, 0f, HotLeanMax);
        float desired = Math.Clamp(driving + lean, FloorPercent, CeilingPercent);
        _currentPercent = Slew(_currentPercent, desired, dt);

        foreach (FanChannel f in controlled)
            f.SetPercent(_currentPercent);

        _engaged = true;

        // Label with the chip that reached the held peak, not the instant hotter one.
        string status = $"Matching {drivingSrc} {driving:F0}°C -> Fans: {_currentPercent:F0}%";
        if (_conflict)
            status += "   ·   another app is fighting for the fans - holding control";

        TrackPeaks(cpu, gpu, controlled);

        _tickCount++;
        if (Environment.TickCount64 - _lastSampleLogMs >= SampleEveryMs)
        {
            _lastSampleLogMs = Environment.TickCount64;
            LogSample(cpu, gpu, temp, controlled, drivingTemp: driving);
        }

        Publish(cpu, gpu, driving, status: status);
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

    /// <summary>A persistent duty mismatch means a foreign writer: tick 4x until quiet for 30s.</summary>
    private void DetectForeignWriter(List<FanChannel> controlled)
    {
        if (!_engaged) return; // nothing of ours on the chip yet

        bool foreign = controlled.Any(f =>
            f.Percent is { } p && Math.Abs(p - _currentPercent) > ForeignWriteTolerance);

        if (foreign)
        {
            _lastForeignWriteMs = Environment.TickCount64;
            _foreignTicks++;

            if (!_conflict && _foreignTicks >= ForeignTicksToConfirm)
            {
                _conflict = true;
                SetTickRate(ConflictTickMs);
                DebugLog.Write(
                    "!! Another program is writing fan speeds over ours (chip readback " +
                    "disagrees with our command). Holding control at 4x re-assert rate. " +
                    "RGB suites (SignalRGB, iCUE, ...) often enable fan control in updates.");
            }
        }
        else
        {
            _foreignTicks = 0;

            if (_conflict && Environment.TickCount64 - _lastForeignWriteMs > ConflictClearMs)
            {
                _conflict = false;
                SetTickRate(TickMs);
                DebugLog.Write("Foreign fan writes stopped - back to the normal tick rate.");
            }
        }
    }

    // Stop/Start dodges the assign-Interval-inside-Elapsed Timers.Timer quirk; no-op once disposed.
    private void SetTickRate(double ms)
    {
        if (System.Threading.Volatile.Read(ref _disposedFlag) != 0) return;
        _timer.Stop();
        _timer.Interval = ms;
        _timer.Start();
    }

    // NaN-safe max fold used by every peak.
    private static void MaxInto(ref float peak, float v)
    {
        if (!float.IsNaN(v) && (float.IsNaN(peak) || v > peak)) peak = v;
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

    private float Slew(float current, float desired, float dt)
    {
        // dt keeps %/second ramps the same at any tick rate.
        float up = SlewUpPerSec * dt;
        float down = SlewDownPerSec * dt;

        float delta = desired - current;
        if (delta > up) delta = up;
        if (delta < -down) delta = -down;
        return current + delta;
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

    private static float? Max(float? a, float? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return MathF.Max(a.Value, b.Value);
    }

    /// <summary>Put the fans back on the BIOS curve; safe from any thread, anytime, and never throws.</summary>
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
        if (System.Threading.Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

        try { _timer.Stop(); } catch { /* shutting down */ }

        // Drain the in-flight tick (2s cap) before the hardware goes away under it.
        bool drained = false;
        for (int i = 0; i < 200; i++)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _tickBusy, 0, 0) == 0) { drained = true; break; }
            Thread.Sleep(10);
        }
        if (!drained) DebugLog.Write("A tick is still stuck in a hardware read after 2s - skipping hardware close.");

        try { _timer.Dispose(); } catch { /* shutting down */ }

        // First, so the session numbers reach disk even if the release hangs.
        try { LogSessionSummary(); } catch { /* never block shutdown */ }

        SafeRelease();

        // Let the fans actually go back before our driver handle goes away.
        Thread.Sleep(400);

        // Closing the library restores OUR saved defaults (the watchdog's seized speed) over its BIOS hand-back.
        bool watchdogOwns;
        lock (_gate) watchdogOwns = _link != null;
        if (watchdogOwns) DebugLog.Write("Watchdog owns the hand-back - leaving the hardware open so it isn't overwritten.");
        else if (drained) _hw.Dispose();
        DebugLog.Write("Controller disposed.");
    }
}
