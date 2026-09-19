using System.Diagnostics;
using System.Management;

namespace FanControlApp.Infrastructure;

/// <summary>
/// Names the process working a chip hardest the moment a peak latches - held in
/// memory only, never written to disk or the debug log.
/// </summary>
public static class PeakCulprit
{
    private const int SampleMs = 500;      // two odometer reads this far apart
    private const long ReuseMs = 5_000;    // a climbing surge = one culprit, not fifty lookups

    private static readonly Lookup Cpu = new(SampleCpuAsync, "CPU");
    private static readonly Lookup Gpu = new(SampleGpuAsync, "GPU");

    /// <summary>Deliver the top CPU consumer's name async (null when nothing
    /// moved), never blocking the fan tick.</summary>
    public static void Identify(Action<string?> deliver) => Cpu.Identify(deliver);

    /// <summary>Same for the GPU: who burned the most GPU-engine time.</summary>
    public static void IdentifyGpu(Action<string?> deliver) => Gpu.Identify(deliver);

    /// <summary>One culprit pipeline: reuse the cached name within a surge,
    /// single-flight the sampling, fan the answer out to every waiter.</summary>
    private sealed class Lookup(Func<Task<string?>> sample, string chip)
    {
        private readonly object _gate = new();
        private readonly List<Action<string?>> _waiters = new();
        private bool _running;
        private long _lastDoneMs;
        private string? _lastName;
        private bool _hasResult;

        public void Identify(Action<string?> deliver)
        {
            lock (_gate)
            {
                if (_hasResult && Environment.TickCount64 - _lastDoneMs < ReuseMs)
                {
                    deliver(_lastName);
                    return;
                }
                _waiters.Add(deliver);
                if (_running) return;
                _running = true;
            }
            _ = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            string? name = null;
            try { name = await sample(); }
            catch (Exception ex)
            {
                // Log the FAILURE only - never a process name.
                DebugLog.Write($"{chip} culprit lookup failed.", ex);
            }

            List<Action<string?>> waiters;
            lock (_gate)
            {
                _lastName = name;
                _lastDoneMs = Environment.TickCount64;
                _hasResult = true;
                _running = false;
                waiters = new List<Action<string?>>(_waiters);
                _waiters.Clear();
            }
            foreach (Action<string?> w in waiters)
            {
                try { w(name); } catch { /* a dead subscriber can't spoil the others */ }
            }
        }
    }

    private static async Task<string?> SampleCpuAsync()
    {
        Dictionary<int, (string Name, TimeSpan Cpu)> first = CpuSnapshot();
        await Task.Delay(SampleMs);
        Dictionary<int, (string Name, TimeSpan Cpu)> second = CpuSnapshot();

        string? name = null;
        TimeSpan best = TimeSpan.Zero;
        foreach (KeyValuePair<int, (string Name, TimeSpan Cpu)> kv in second)
        {
            if (!first.TryGetValue(kv.Key, out (string Name, TimeSpan Cpu) was)) continue;
            if (was.Name != kv.Value.Name) continue; // pid reused mid-sample
            TimeSpan delta = kv.Value.Cpu - was.Cpu;
            if (delta > best) { best = delta; name = kv.Value.Name; }
        }
        return name;
    }

    private static Dictionary<int, (string Name, TimeSpan Cpu)> CpuSnapshot()
    {
        var map = new Dictionary<int, (string, TimeSpan)>();
        foreach (Process p in Process.GetProcesses())
        {
            try { map[p.Id] = (p.ProcessName, p.TotalProcessorTime); }
            catch { /* protected or already-gone processes have no story to tell */ }
            finally { p.Dispose(); }
        }
        return map;
    }

    // The CPU dance again, over Windows' per-process GPU-time odometers - ALL
    // engine types count, so an encode pegging the video block is still caught.
    private static async Task<string?> SampleGpuAsync()
    {
        Dictionary<int, long> first = GpuSnapshot();
        await Task.Delay(SampleMs);
        Dictionary<int, long> second = GpuSnapshot();

        int bestPid = 0;
        long best = 0;
        foreach (KeyValuePair<int, long> kv in second)
        {
            if (!first.TryGetValue(kv.Key, out long was)) continue;
            long delta = kv.Value - was;
            if (delta > best) { best = delta; bestPid = kv.Key; }
        }
        if (bestPid == 0) return null; // nobody moved - a truly idle GPU has no culprit
        try
        {
            using var p = Process.GetProcessById(bestPid);
            return p.ProcessName;
        }
        catch { return null; } // winner exited between sample and name lookup
    }

    /// <summary>GPU running time per pid, summed across that process's engines.</summary>
    private static Dictionary<int, long> GpuSnapshot()
    {
        var map = new Dictionary<int, long>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, RunningTime FROM Win32_PerfRawData_GPUPerformanceCounters_GPUEngine");
        using ManagementObjectCollection rows = searcher.Get();
        foreach (ManagementBaseObject row in rows)
        {
            using (row)
            {
                // Instance names look like: pid_1234_luid_..._engtype_3D
                if (row["Name"] is not string inst
                    || !inst.StartsWith("pid_", StringComparison.Ordinal)) continue;
                int end = inst.IndexOf('_', 4);
                if (end < 0 || !int.TryParse(inst.AsSpan(4, end - 4), out int pid)) continue;
                long rt = Convert.ToInt64(row["RunningTime"]);
                map[pid] = map.TryGetValue(pid, out long sum) ? sum + rt : rt;
            }
        }
        return map;
    }
}
