using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace FanControlApp.Controls;

/// <summary>
/// A horizontal readout bar - the "sensor panel" counterpart to the dials. Same
/// visual language as <see cref="Gauge"/>: dark machined track, green/red zones
/// expressed through the fill colour, and a yellow peak-this-run tick. One bar
/// shows one number; a second, smaller reading (a fan's RPM) can ride inside
/// the free end of the track.
/// </summary>
public partial class StatBar : UserControl
{
    private double _peak = double.NaN;

    public StatBar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateVisual();
    }

    // ---- properties ---------------------------------------------------------

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatBar),
            new PropertyMetadata("", (d, _) => ((StatBar)d).OnLabelChanged()));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(StatBar),
            new PropertyMetadata(double.NaN, (d, _) => ((StatBar)d).UpdateVisual()));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(StatBar),
            new PropertyMetadata("", (d, _) => ((StatBar)d).UpdateVisual()));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(StatBar),
            new PropertyMetadata(0.0, (d, _) => ((StatBar)d).UpdateVisual()));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(StatBar),
            new PropertyMetadata(100.0, (d, _) => ((StatBar)d).UpdateVisual()));

    public static readonly DependencyProperty GreenToProperty =
        DependencyProperty.Register(nameof(GreenTo), typeof(double), typeof(StatBar),
            new PropertyMetadata(double.NaN, (d, _) => ((StatBar)d).UpdateVisual()));

    public static readonly DependencyProperty RedFromProperty =
        DependencyProperty.Register(nameof(RedFrom), typeof(double), typeof(StatBar),
            new PropertyMetadata(double.NaN, (d, _) => ((StatBar)d).UpdateVisual()));

    public static readonly DependencyProperty TrackPeakProperty =
        DependencyProperty.Register(nameof(TrackPeak), typeof(bool), typeof(StatBar),
            new PropertyMetadata(false));

    /// <summary>Name on the left. Long fan names ellipsize with a tooltip.</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>The reading. NaN renders an empty bar and "--".</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>Shown after the number: "C" renders as "47 C", "%" as "47%".</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>Values at or below this fill green; NaN = no green zone.</summary>
    public double GreenTo { get => (double)GetValue(GreenToProperty); set => SetValue(GreenToProperty, value); }

    /// <summary>Values at or above this fill red; NaN = no red zone.</summary>
    public double RedFrom { get => (double)GetValue(RedFromProperty); set => SetValue(RedFromProperty, value); }

    /// <summary>Remember and mark the highest value this run (temps yes, fans no).</summary>
    public bool TrackPeak { get => (bool)GetValue(TrackPeakProperty); set => SetValue(TrackPeakProperty, value); }

    /// <summary>Secondary reading inside the track's free end (a fan's live RPM).</summary>
    public string SecondaryText { set => InBarText.Text = value; }

    public void ResetPeak()
    {
        _peak = double.NaN;
        PeakTick.Visibility = Visibility.Collapsed;
    }

    // ---- rendering ----------------------------------------------------------

    private void OnLabelChanged()
    {
        LabelText.Text = Label;
        LabelText.ToolTip = Label;
    }

    private void UpdateVisual()
    {
        double w = TrackHost.ActualWidth - 2; // the fill sits inside the 1px frame
        if (w <= 0) return;

        double v = Value;
        bool has = !double.IsNaN(v);

        // The number, coloured exactly like the dials colour theirs: green in the
        // green zone, red at the redline, white in between (or when zoneless).
        string unit = Unit == "%" ? "%" : string.IsNullOrEmpty(Unit) ? "" : $" {Unit}";
        ValueText.Text = has ? $"{v:F0}{unit}" : "--";
        ValueText.Foreground = ZoneBrush(v, has);

        double frac = has ? Math.Clamp((v - Minimum) / (Maximum - Minimum), 0, 1) : 0;
        Fill.Width = frac * w;
        Fill.Background = FillBrush(v, has);
        Fill.Effect = has && frac > 0 ? Glow(((SolidColorBrush)ZoneBrush(v, has)).Color) : null;

        if (TrackPeak && has && (double.IsNaN(_peak) || v > _peak))
            _peak = v;

        if (TrackPeak && !double.IsNaN(_peak))
        {
            double pf = Math.Clamp((_peak - Minimum) / (Maximum - Minimum), 0, 1);
            PeakTick.Margin = new Thickness(Math.Max(0, pf * w - 1), 0, 0, 0);
            PeakTick.ToolTip = $"Peak this run: {_peak:F0}{unit}";
            PeakTick.Visibility = Visibility.Visible;
        }
    }

    private Brush ZoneBrush(double v, bool has)
    {
        if (!has) return B("#FFFFFF");
        if (!double.IsNaN(RedFrom) && v >= RedFrom) return B("#F85149");
        if (!double.IsNaN(GreenTo) && v <= GreenTo) return B("#3FB950");
        if (!double.IsNaN(GreenTo) || !double.IsNaN(RedFrom)) return B("#E3B341");
        return B("#AEB6C6"); // zoneless (fan %): neutral steel, same as the needle metal
    }

    private Brush FillBrush(double v, bool has)
    {
        Color c = ((SolidColorBrush)ZoneBrush(v, has)).Color;
        // Lit from the top like everything else on the dash.
        return new LinearGradientBrush(
            Color.FromRgb((byte)Math.Min(255, c.R + 40), (byte)Math.Min(255, c.G + 40), (byte)Math.Min(255, c.B + 40)),
            Color.FromRgb((byte)(c.R * 0.55), (byte)(c.G * 0.55), (byte)(c.B * 0.55)),
            90);
    }

    private static DropShadowEffect Glow(Color c) => new()
    {
        Color = c, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.45,
    };

    private static SolidColorBrush B(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex)!);
}
