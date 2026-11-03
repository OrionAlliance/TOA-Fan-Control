using System.IO;

namespace FanControlApp.Infrastructure;

/// <summary>Every file the app keeps lives next to the exe, so it stays portable; only an unlocked copy's installer downloads use Temp.</summary>
public static class AppPaths
{
    public static string ExeDir { get; } = AppContext.BaseDirectory;

    /// <summary>Config files live together in Settings\.</summary>
    public static string SettingsDir { get; } = Path.Combine(ExeDir, "Settings");

    public static string SettingsFile { get; } = Path.Combine(SettingsDir, "fan_settings.json");

    public static void EnsureSettingsDir() => Directory.CreateDirectory(SettingsDir);

    /// <summary>The running app holds this; Setup checks it to know the app is open.</summary>
    public const string InstanceMutexName = @"Global\TOA.FanControl.Instance";

    public static bool AppIsRunning()
    {
        if (!System.Threading.Mutex.TryOpenExisting(InstanceMutexName, out System.Threading.Mutex? m)) return false;
        m.Dispose();
        return true;
    }
}
