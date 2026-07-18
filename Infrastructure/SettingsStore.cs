using System.IO;
using System.Text.Json;

namespace FanControlApp.Infrastructure;

/// <summary>
/// What little there is to remember. The behaviour isn't configurable - the app
/// drives every case fan (all of them, whatever the board has), skips pumps and
/// CPU coolers by name, and matches fan % to the hotter of CPU/GPU, floored at
/// 30%. Which fans to drive is worked out live from the hardware, not stored - so
/// nothing here is machine-specific. All that persists is the overlay position.
/// </summary>
public sealed class FanSettings
{
    /// <summary>
    /// Where the Game Mode overlay was left. NaN = never placed, so it starts
    /// top-centre. Worth persisting: you position it once around your HUD and
    /// never want to think about it again.
    /// </summary>
    public double OverlayLeft { get; set; } = double.NaN;
    public double OverlayTop { get; set; } = double.NaN;
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static FanSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                DebugLog.Write("No settings file; using defaults.");
                return new FanSettings();
            }

            string json = File.ReadAllText(AppPaths.SettingsFile);
            FanSettings? s = JsonSerializer.Deserialize<FanSettings>(json, Options);
            if (s == null)
            {
                DebugLog.Write("Settings file deserialized to null; using defaults.");
                return new FanSettings();
            }

            DebugLog.Write("Settings loaded.");
            return s;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Settings load failed; using defaults.", ex);
            return new FanSettings();
        }
    }

    public static void Save(FanSettings settings)
    {
        try
        {
            AppPaths.EnsureSettingsDir();
            string json = JsonSerializer.Serialize(settings, Options);
            File.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Settings save failed.", ex);
        }
    }
}
