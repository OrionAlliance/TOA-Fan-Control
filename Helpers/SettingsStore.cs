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
    public FanMode Mode { get; set; } = FanMode.Auto;

    /// <summary>
    /// CPU, deliberately. "Hotter of the two" looks sensible but isn't: an AMD
    /// GPU's Hot Spot normally runs 90-100C under load, which is fine for the
    /// card but would sit above the CPU's panic line permanently and peg the
    /// fans forever. The two numbers aren't on the same scale.
    /// </summary>
    public TempSource Source { get; set; } = TempSource.Cpu;

    public FanCurve Curve { get; set; } = FanCurve.Default();

    /// <summary>
    /// Auto mode holds the source temp at (or below) this. 85C is the user's
    /// call: the 5800X throttles at 90, so this keeps a few degrees of headroom
    /// while leaving the fans quiet until the heat actually matters.
    /// </summary>
    public float TargetTemp { get; set; } = 85f;

    public float MinPercent { get; set; } = 30f;
    public float MaxPercent { get; set; } = 100f;

    /// <summary>
    /// Above this, everything goes to full regardless of mode. Sits just under
    /// the 5800X's 90C throttle point, and far enough above the 85C target that
    /// a normal transient spike doesn't slam the fans to full mid-game.
    /// </summary>
    public float PanicTemp { get; set; } = 89f;

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
