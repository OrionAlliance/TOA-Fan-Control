using System.IO;
using System.Text.Json;

namespace FanControlApp.Infrastructure;

/// <summary>Persisted user choices; fan behaviour itself is fixed and not configurable.</summary>
public sealed class FanSettings
{
    /// <summary>Game Mode overlay position; NaN = never placed, starts top centre.</summary>
    public double OverlayLeft { get; set; } = double.NaN;
    public double OverlayTop { get; set; } = double.NaN;

    /// <summary>"Dark" or "Light" theme.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>"Dials" (gauges) or "Bars" (compact rows); display only.</summary>
    public string DisplayStyle { get; set; } = "Dials";

    /// <summary>Fans the user allowed; null shows the picker. Pumps and CPU/GPU coolers are excluded first.</summary>
    public List<string>? SelectedFans { get; set; }

    /// <summary>Card the "GPU not in library" notice was shown for, so it fires once per card.</summary>
    public string? GpuNoticeShownFor { get; set; }

    /// <summary>User max watts, keyed by card name so a GPU swap never inherits it; outranks the library.</summary>
    public string? GpuUserMaxWattsFor { get; set; }
    public int? GpuUserMaxWatts { get; set; }

    /// <summary>The one-time "Start with Windows?" question was already asked.</summary>
    public bool StartupOffered { get; set; }

    /// <summary>Fan headers already decided on, so the new-fan popup skips them; null until first set up.</summary>
    public List<string>? AskedFans { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Overlay position defaults to NaN, which System.Text.Json rejects unless allowed.
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling
            .AllowNamedFloatingPointLiterals,
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
