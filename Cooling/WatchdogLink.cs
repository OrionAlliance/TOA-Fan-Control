using System.Diagnostics;
using System.Threading;

namespace FanControlApp.Cooling;

/// <summary>The three signals between the app and its watchdog: the app asks, the watchdog acts.</summary>
public sealed class WatchdogLink : IDisposable
{
    /// <summary>Set by the watchdog once it holds the fans (and the real defaults).</summary>
    public EventWaitHandle Ready { get; }

    /// <summary>Set by the app to ask for the fans to go back to the BIOS.</summary>
    public EventWaitHandle Restore { get; }

    /// <summary>Set by the app to ask the watchdog to take them again.</summary>
    public EventWaitHandle Resume { get; }

    /// <summary>The sentinel process, set by the app after launching it.</summary>
    public Process? Sentinel { get; set; }

    /// <summary>False once the sentinel is gone; check every tick since the events outlive it.</summary>
    public bool SentinelAlive
    {
        get
        {
            try { return Sentinel is { HasExited: false }; }
            catch { return false; }
        }
    }

    private WatchdogLink(EventWaitHandle ready, EventWaitHandle restore, EventWaitHandle resume)
    {
        Ready = ready;
        Restore = restore;
        Resume = resume;
    }

    // Scoped to the app's pid so a stale watchdog from a previous run can't answer.
    private static string ReadyName(int pid) => $@"Local\TOA_FanControl_{pid}_Ready";
    private static string RestoreName(int pid) => $@"Local\TOA_FanControl_{pid}_Restore";
    private static string ResumeName(int pid) => $@"Local\TOA_FanControl_{pid}_Resume";

    /// <summary>App side: create the signals before launching the watchdog.</summary>
    public static WatchdogLink Create(int pid) => new(
        new EventWaitHandle(false, EventResetMode.ManualReset, ReadyName(pid)),
        new EventWaitHandle(false, EventResetMode.AutoReset, RestoreName(pid)),
        new EventWaitHandle(false, EventResetMode.AutoReset, ResumeName(pid)));

    /// <summary>Watchdog side: attach to the signals the app already made.</summary>
    public static WatchdogLink Open(int pid) => new(
        EventWaitHandle.OpenExisting(ReadyName(pid)),
        EventWaitHandle.OpenExisting(RestoreName(pid)),
        EventWaitHandle.OpenExisting(ResumeName(pid)));

    public void Dispose()
    {
        Ready.Dispose();
        Restore.Dispose();
        Resume.Dispose();
    }
}
