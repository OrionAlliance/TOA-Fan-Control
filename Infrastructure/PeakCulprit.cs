using System.Diagnostics;

namespace FanControlApp.Infrastructure;

/// <summary>
/// Names the process working the CPU hardest RIGHT NOW - asked the moment a CPU
/// peak latches, so the Report can say who set it. The name lives in memory
/// only: never written to disk and never to the debug log (privacy rule - the
/// one file a user might share stays free of what they run).
/// </summary>
public static class PeakCulprit
{
    private const int SampleMs = 500;      // two odometer reads this far apart
    private const long ReuseMs = 5_000;    // a climbing surge = one culprit, not fifty lookups

    private static readonly object Gate = new();
    private static readonly List<Action<string>> Waiters = new();
    private static bool _running;
    private static long _lastDoneMs;
    private static string? _lastName;

    /// <summary>Deliver the current top CPU consumer's name (async, ~0.5s). Callers
    /// during the same surge share one lookup; the fan tick is never blocked.</summary>
    public static void Identify(Action<string> deliver)
    {
        lock (Gate)
        {
            if (_lastName != null && Environment.TickCount64 - _lastDoneMs < ReuseMs)
            {
                deliver(_lastName);
                return;
            }
            Waiters.Add(deliver);
            if (_running) return;
            _running = true;
        }
        _ = Task.Run(SampleAsync);
    }

    private static async Task SampleAsync()
    {
        string name = "unknown";
        try
        {
            Dictionary<int, (string Name, TimeSpan Cpu)> first = Snapshot();
            await Task.Delay(SampleMs);
            Dictionary<int, (string Name, TimeSpan Cpu)> second = Snapshot();

            TimeSpan best = TimeSpan.Zero;
            foreach (KeyValuePair<int, (string Name, TimeSpan Cpu)> kv in second)
            {
                if (!first.TryGetValue(kv.Key, out (string Name, TimeSpan Cpu) was)) continue;
                if (was.Name != kv.Value.Name) continue; // pid reused mid-sample
                TimeSpan delta = kv.Value.Cpu - was.Cpu;
                if (delta > best) { best = delta; name = kv.Value.Name; }
            }
        }
        catch (Exception ex)
        {
            // Log the FAILURE only - never a process name.
            DebugLog.Write("Peak culprit lookup failed.", ex);
        }

        List<Action<string>> waiters;
        lock (Gate)
        {
            _lastName = name;
            _lastDoneMs = Environment.TickCount64;
            _running = false;
            waiters = new List<Action<string>>(Waiters);
            Waiters.Clear();
        }
        foreach (Action<string> w in waiters)
        {
            try { w(name); } catch { /* a dead subscriber can't spoil the others */ }
        }
    }

    private static Dictionary<int, (string Name, TimeSpan Cpu)> Snapshot()
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
}
