using System.IO;

namespace FanControlApp.Infrastructure;

/// <summary>Every file the app writes lives next to the exe, never AppData, so it stays portable.</summary>
public static class AppPaths
{
    public static string ExeDir { get; } = AppContext.BaseDirectory;

    /// <summary>Config files live together in Settings\.</summary>
    public static string SettingsDir { get; } = Path.Combine(ExeDir, "Settings");

    public static string SettingsFile { get; } = Path.Combine(SettingsDir, "fan_settings.json");

    public static void EnsureSettingsDir() => Directory.CreateDirectory(SettingsDir);
}
