using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace FanControlApp.Controls;

/// <summary>
/// A horizontal readout bar - the "sensor panel" counterpart to the dials, styled
/// after the classic AIDA strip displays: every reading lives INSIDE the bar
/// (name left, value right, a fan's RPM just before the value). Same visual
/// language as <see cref="Gauge"/>: dark machined track, zone-coloured fill
/// (green/amber/red for temps, neutral steel for fan %), yellow peak tick.
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

    /// <summary>Name inside the bar's left end. Long fan names ellipsize with a tooltip.</summary>
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

    /// <summary>
    /// The session peak, fed by the controller - one truth shared by every view.
    /// NaN hides the marker (fan bars simply never get one).
    /// </summary>
    public double Peak
    {
        get => _peak;
        set
        {
            if (value.Equals(_peak) || (double.IsNaN(value) && double.IsNaN(_peak))) return;
            _peak = value;
            UpdateVisual();
        }
    }

    // ---- rendering ----------------------------------------------------------

    private void OnLabelChanged()
    {
        LabelTextW.Text = Label;
        LabelTextB.Text = Label;
        LabelTextW.ToolTip = Label;
        LabelTextB.ToolTip = Label;
    }

    private void UpdateVisual()
    {
        double w = TrackHost.ActualWidth - 2; // the fill sits inside the 1px frame
        if (w <= 0) return;

        double v = Value;
        bool has = !double.IsNaN(v);

        // The number outside the bar, coloured exactly like the dials colour
        // theirs: zone colour for temps, the theme's text colour when zoneless.
        string unit = Unit == "%" ? "%" : string.IsNullOrEmpty(Unit) ? "" : $" {Unit}";
        ValueText.Text = has ? $"{v:F0}{unit}" : "--";
        if (double.IsNaN(GreenTo) && double.IsNaN(RedFrom))
            ValueText.SetResourceReference(ForegroundProperty, "Text");
        else
            ValueText.Foreground = new SolidColorBrush(ZoneColor(v, has));

        double frac = has ? Math.Clamp((v - Minimum) / (Maximum - Minimum), 0, 1) : 0;
        Fill.Width = frac * w;
        Fill.Background = FillBrush(v, has);
        Fill.Effect = has && frac > 0 ? Glow(ZoneColor(v, has)) : null;

        // Black text exists only over the fill: the black layer is clipped to the
        // fill's width, and the white layer shows past its edge.
        TextBlackLayer.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, Math.Max(0, Fill.Width + 1), TrackHost.ActualHeight));

        if (!double.IsNaN(_peak))
        {
            double pf = Math.Clamp((_peak - Minimum) / (Maximum - Minimum), 0, 1);
            PeakTick.Margin = new Thickness(Math.Max(0, pf * w - 1), 1, 0, 1);
            PeakTick.ToolTip = $"Peak this run: {_peak:F0}{unit}";
            PeakTick.Visibility = Visibility.Visible;
        }
        else
        {
            PeakTick.Visibility = Visibility.Collapsed;
        }
    }

    private Color ZoneColor(double v, bool has)
    {
        if (!has) return C("#AEB6C6");
        if (!double.IsNaN(RedFrom) && v >= RedFrom) return C("#F85149");
        if (!double.IsNaN(GreenTo) && v <= GreenTo) return C("#3FB950");
        if (!double.IsNaN(GreenTo) || !double.IsNaN(RedFrom)) return C("#E3B341");
        return C("#AEB6C6"); // zoneless (fan %): neutral steel, same as the needle metal
    }

    private Brush FillBrush(double v, bool has)
    {
        Color c = ZoneColor(v, has);
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

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
}
