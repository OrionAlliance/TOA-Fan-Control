using System.Windows;
using System.Windows.Media;

namespace FanControlApp.Infrastructure;

public enum AppTheme { Dark, Light }

/// <summary>
/// Swaps the chrome palette (window, cards, buttons, text, menus) between dark and
/// light by overwriting the named brushes in Application.Resources - everything
/// references them via DynamicResource, so the switch is live. The gauge dials and
/// fan tiles are deliberately NOT themed: they're drawn as physical instruments,
/// and a car dash is dark in any theme. Semantic colours (Cool/Warm/Hot, peak
/// yellow, close-button red) never change either - they're information.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Raised after a swap, so windows can re-paint their DWM title bars.</summary>
    public static event EventHandler? Changed;

    public static void Apply(string? name) =>
        Apply(string.Equals(name, "Light", StringComparison.OrdinalIgnoreCase)
            ? AppTheme.Light : AppTheme.Dark);

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        ResourceDictionary r = Application.Current.Resources;

        if (theme == AppTheme.Dark)
        {
            r["Bg"] = Solid("#12141A");
            r["Panel"] = Solid("#1A1D26");
            r["PanelEdge"] = Solid("#2A2F3D");
            r["Text"] = Solid("#FFFFFF");
            r["TextDim"] = Solid("#FFFFFF");

            r["TitleFace"] = Grad(("#2A3040", 0), ("#1E222D", 0.55), ("#141720", 1));
            r["CardFace"] = Grad(("#232834", 0), ("#1A1D26", 0.5), ("#15181F", 1));
            r["CardEdge"] = Grad(("#454E63", 0), ("#2A2F3D", 0.5), ("#0E1116", 1));

            r["BtnFace"] = Grad(("#333A4C", 0), ("#242938", 1));
            r["BtnFaceHover"] = Grad(("#414A61", 0), ("#2F3549", 1));
            r["BtnFacePressed"] = Grad(("#1C2130", 0), ("#2A3042", 1));
            r["BtnEdge"] = Grad(("#4E576E", 0), ("#11141B", 1));

            r["CapBtnHover"] = Solid("#333B4D");
            r["CapBtnPressed"] = Solid("#454E63");

            // Bar view: dark machined groove, matching the dials' faces.
            r["BarTrack"] = Grad(("#0C0E13", 0), ("#191D27", 0.55), ("#232735", 1));
            r["BarTrackEdge"] = Solid("#323848");
            r["BarTrackText"] = Solid("#FFFFFF");
        }
        else
        {
            r["Bg"] = Solid("#E9EBEF");
            r["Panel"] = Solid("#F4F5F8");
            r["PanelEdge"] = Solid("#C0C6D1");
            r["Text"] = Solid("#14171F");
            r["TextDim"] = Solid("#14171F");

            r["TitleFace"] = Grad(("#F4F6F9", 0), ("#E7EAF0", 0.55), ("#D9DDE5", 1));
            r["CardFace"] = Grad(("#FCFDFE", 0), ("#EFF1F5", 0.5), ("#E6E9EE", 1));
            r["CardEdge"] = Grad(("#FFFFFF", 0), ("#C7CDD8", 0.5), ("#9BA3B2", 1));

            r["BtnFace"] = Grad(("#FBFCFD", 0), ("#E4E8EE", 1));
            r["BtnFaceHover"] = Grad(("#FFFFFF", 0), ("#EDF0F5", 1));
            r["BtnFacePressed"] = Grad(("#D9DEE6", 0), ("#E6EAF0", 1));
            r["BtnEdge"] = Grad(("#FFFFFF", 0), ("#A6ADBC", 1));

            r["CapBtnHover"] = Solid("#D9DDE5");
            r["CapBtnPressed"] = Solid("#C0C6D1");

            // Bar view: a recessed light groove instead of the dark one - naked
            // dark tracks on a light card read as holes, not instruments.
            r["BarTrack"] = Grad(("#C6CBD6", 0), ("#DDE1E8", 0.55), ("#EDEFF4", 1));
            r["BarTrackEdge"] = Solid("#A9B0BF");
            r["BarTrackText"] = Solid("#14171F");
        }

        DebugLog.Write($"Theme applied: {theme}.");
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static SolidColorBrush Solid(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush Grad(params (string Hex, double Offset)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach ((string hex, double off) in stops)
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hex)!, off));
        b.Freeze();
        return b;
    }
}
