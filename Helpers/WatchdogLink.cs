using System.Diagnostics;
using System.Threading;

namespace FanControlApp.Helpers;

/// <summary>
/// The three signals between the app and its watchdog.
///
/// Why any of this exists: the fan chip has no "give control back to the BIOS"
/// command. The library restores a header by writing back the register values it
/// read the first time it took that header - so only the process that grabbed the
/// fans FIRST is holding the real BIOS settings. Everyone else has nothing to
/// restore.
///
/// So the watchdog grabs them first and is the sole owner of handing them back.
/// The app asks; the watchdog acts.
/// </summary>
public sealed class WatchdogLink : IDisposable
{
    /// <summary>Set by the watchdog once it holds the fans (and the real defaults).</summary>
    public EventWaitHandle Ready { get; }

    /// <summary>Set by the app to ask for the fans to go back to the BIOS.</summary>
    public EventWaitHandle Restore { get; }

    /// <summary>Set by the app to ask the watchdog to take them again.</summary>
    public EventWaitHandle Resume { get; }

    /// <summary>The sentinel process itself. Set by the app after launching it.</summary>
    public Process? Sentinel { get; set; }

    /// <summary>
    /// False once the sentinel is gone. Worth checking every tick: these events
    /// keep working after the process behind them dies - Restore would be set for
    /// nobody, and Ready is manual-reset so it stays signalled forever. The app
    /// would carry on driving and quietly lose any way to hand the fans back.
    /// </summary>
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

    // Scoped to the app's pid so a stale watchdog from a previous run can never
    // answer for this one.
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
