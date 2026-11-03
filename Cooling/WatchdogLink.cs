using System.Diagnostics;

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

    // Names carry the app's pid plus a random token.
    private static string Name(int pid, string token, string what) => $@"Local\TOA_FanControl_{pid}_{token}_{what}";

    /// <summary>A fresh random token for one app run; passed to the watchdog on its command line.</summary>
    public static string NewToken() => Guid.NewGuid().ToString("N");

    /// <summary>True for a token this app could have made (32 hex digits).</summary>
    public static bool IsToken(string s) => s.Length == 32 && s.All(Uri.IsHexDigit);

    /// <summary>App side: create the signals before launching the watchdog; refuses names that already exist.</summary>
    public static WatchdogLink Create(int pid, string token) => new(
        CreateNew(Name(pid, token, "Ready"), EventResetMode.ManualReset),
        CreateNew(Name(pid, token, "Restore"), EventResetMode.AutoReset),
        CreateNew(Name(pid, token, "Resume"), EventResetMode.AutoReset));

    // Fails if the name already exists.
    private static EventWaitHandle CreateNew(string name, EventResetMode mode)
    {
        var e = new EventWaitHandle(false, mode, name, out bool createdNew);
        if (createdNew) return e;
        e.Dispose();
        throw new InvalidOperationException("a watchdog signal already existed");
    }

    /// <summary>Watchdog side: attach to the signals the app already made.</summary>
    public static WatchdogLink Open(int pid, string token) => new(
        EventWaitHandle.OpenExisting(Name(pid, token, "Ready")),
        EventWaitHandle.OpenExisting(Name(pid, token, "Restore")),
        EventWaitHandle.OpenExisting(Name(pid, token, "Resume")));

    public void Dispose()
    {
        Ready.Dispose();
        Restore.Dispose();
        Resume.Dispose();
    }
}
