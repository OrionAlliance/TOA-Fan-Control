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

    /// <summary>"Dark" or "Light" - the chrome palette picked in Settings.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>
    /// "Dials" (car-dash gauges + spinning fan tiles) or "Bars" (compact
    /// sensor-panel rows). Pure display taste - the fan behaviour is identical.
    /// </summary>
    public string DisplayStyle { get; set; } = "Dials";

    /// <summary>
    /// The fans the user confirmed the app may drive (Settings → Choose fans, or
    /// the first-run picker). Null = never picked yet, which triggers the picker.
    /// Only ever narrows the built-in safety rule - fans named like pumps/CPU/GPU
    /// coolers are excluded before this list is even consulted.
    /// </summary>
    public List<string>? SelectedFans { get; set; }

    /// <summary>
    /// The card name the "your GPU isn't in the library yet" notice was already
    /// shown for - so it fires once per distinct card, never nags. Cleared when
    /// that card later gains a library row, which triggers the one-time
    /// "now supported" balloon instead. Null = nothing shown or pending.
    /// </summary>
    public string? GpuNoticeShownFor { get; set; }

    /// <summary>
    /// User-entered max watts for an unlisted card, keyed by the card's name so
    /// a GPU swap can never inherit a stale number. The user's value outranks
    /// the library - they know their exact card, the library knows the reference.
    /// </summary>
    public string? GpuUserMaxWattsFor { get; set; }
    public int? GpuUserMaxWatts { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // OverlayLeft/Top are NaN until the overlay is first placed, and
        // System.Text.Json rejects NaN by default - which made BOTH save and load
        // throw (caught + logged, so it just looked like settings never stuck).
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
