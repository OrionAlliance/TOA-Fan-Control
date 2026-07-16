using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanControlApp.Helpers;

public enum FanMode
{
    /// <summary>Follow the curve the user drew.</summary>
    Manual,

    /// <summary>Watch the temp and adapt the fans to hold a target.</summary>
    Auto,
}

public enum TempSource
{
    Cpu,
    Gpu,

    /// <summary>Whichever of CPU/GPU is hotter right now. Case fans clear heat from both.</summary>
    Hotter,
}

public sealed class FanSettings
{
    public FanMode Mode { get; set; } = FanMode.Manual;
    public TempSource Source { get; set; } = TempSource.Hotter;

    public FanCurve Curve { get; set; } = FanCurve.Default();

    /// <summary>Auto mode holds the source temp at (or below) this.</summary>
    public float TargetTemp { get; set; } = 65f;

    public float MinPercent { get; set; } = 30f;
    public float MaxPercent { get; set; } = 100f;

    /// <summary>Above this, everything goes to full regardless of mode.</summary>
    public float PanicTemp { get; set; } = 85f;

    /// <summary>Only these headers are ever written to. Everything else stays on the BIOS curve.</summary>
    public List<string> ControlledFans { get; set; } = new() { "Chassis Fan #2", "Chassis Fan #3" };
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
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

            if (s.Curve.Points.Count == 0) s.Curve = FanCurve.Default();
            DebugLog.Write($"Settings loaded: mode={s.Mode} source={s.Source} target={s.TargetTemp}");
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
