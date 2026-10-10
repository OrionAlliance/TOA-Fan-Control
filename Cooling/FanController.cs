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
public sealed partial class FanController : IDisposable
{
    private const double TickMs = 1000;

    // During a conflict, re-assert every 250ms; the Super I/O has no concept of ownership.
    private const double ConflictTickMs = 250;

    // Hard 30% floor, deliberately not a setting: below it Chassis Fan #2 stalls to 0 RPM.
    private const float FloorPercent = 30f;
    private const float CeilingPercent = 100f;

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
}
