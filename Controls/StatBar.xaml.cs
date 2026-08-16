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
    private double _peakLoad = double.NaN;
    private double _loadValue = double.NaN;
    private bool _trueLoad;

    /// <summary>True = the load lane shows real load (watts vs the card's max);
    /// false = busy time. Only changes the words - a bar must say what it measures.</summary>
    public bool TrueLoad
    {
        set
        {
            if (value == _trueLoad) return;
            _trueLoad = value;
            if (!double.IsNaN(_loadValue))
                LoadTri.ToolTip = $"{CapLoadWord} right now: {_loadValue:F0}%";
            if (!double.IsNaN(_peakLoad))
                LoadPeakTick.ToolTip = $"Peak {LoadWord} this run: {_peakLoad:F0}%";
        }
    }

    private string LoadWord => _trueLoad ? "load" : "busy time";
    private string CapLoadWord => _trueLoad ? "Load" : "Busy time";

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

    /// <summary>Live load % right now (0-100) - the cyan triangle slides with it,
    /// the load lane's needle. NaN hides the triangle.</summary>
    public double LoadValue
    {
        set
        {
            if (value.Equals(_loadValue)) return; // double.Equals: NaN equals NaN
            _loadValue = value;
            LoadTri.ToolTip = double.IsNaN(value) ? null : $"{CapLoadWord} right now: {value:F0}%";
            UpdateVisual();
        }
    }

    /// <summary>Independent session peak load % (0-100), fed by the controller -
    /// the cyan tick that stays at the highest point the triangle reached.
    /// NaN hides the tick.</summary>
    public double PeakLoad
    {
        set
        {
            if (value.Equals(_peakLoad)) return; // double.Equals: NaN equals NaN
            _peakLoad = value;
            LoadPeakTick.ToolTip = double.IsNaN(value) ? null : $"Peak {LoadWord} this run: {value:F0}%";
            UpdateVisual();
        }
    }

    /// <summary>
    /// The session peak, fed by the controller - one truth shared by every view.
    /// NaN hides the marker (fan bars simply never get one).
    /// </summary>
    public double Peak
    {
        get => _peak;
        set
        {
            if (value.Equals(_peak)) return; // double.Equals: NaN equals NaN
            _peak = value;
            string unit = Unit == "%" ? "%" : string.IsNullOrEmpty(Unit) ? "" : $" {Unit}";
            PeakTick.ToolTip = double.IsNaN(value) ? null : $"Peak temp this run: {value:F0}{unit}";
            UpdateVisual();
        }
    }

    // ---- rendering ----------------------------------------------------------

    /// <summary>
    /// Live secondary reading shown right after the value ("37%  RPM: 706").
    /// Null/empty hides it and gives the bar the space back. The value column
    /// is content-sized, so the bar always gets every pixel the text doesn't use.
    /// </summary>
    public string? TrailText
    {
        set
        {
            bool show = !string.IsNullOrEmpty(value);
            TrailTextBlock.Text = value ?? "";
            TrailTextBlock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

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

        Color zone = ZoneColor(v, has);
        if (double.IsNaN(GreenTo) && double.IsNaN(RedFrom))
            ValueText.SetResourceReference(ForegroundProperty, "Text");
        else
            ValueText.Foreground = Solid(zone);

        double frac = has ? Math.Clamp((v - Minimum) / (Maximum - Minimum), 0, 1) : 0;
        Fill.Width = frac * w;

        // Paint only changes when the value crosses a zone line - reuse the
        // frozen cached brushes instead of mixing fresh paint every tick.
        Fill.Background = FillFor(zone);
        Fill.Effect = has && frac > 0 ? GlowFor(zone) : null;

        // Black text exists only over the fill: the black layer is clipped to the
        // fill's width, and the white layer shows past its edge. One geometry,
        // resized in place.
        _clip.Rect = new Rect(0, 0, Math.Max(0, Fill.Width + 1), TrackHost.ActualHeight);
        TextBlackLayer.Clip ??= _clip;

        // Marker tooltips are set in the Peak/PeakLoad setters - only position
        // and visibility belong here, so value ticks allocate nothing.
        if (!double.IsNaN(_peak))
        {
            double pf = Math.Clamp((_peak - Minimum) / (Maximum - Minimum), 0, 1);
            PeakTick.Margin = new Thickness(Math.Max(0, pf * w - 1), 1, 0, 1);
            PeakTick.Visibility = Visibility.Visible;
        }
        else
        {
            PeakTick.Visibility = Visibility.Collapsed;
        }

        // Load is a % of the WHOLE track (a tachometer fraction), not a point
        // on the temperature axis - identical only while the bar runs 0-100.
        if (!double.IsNaN(_peakLoad))
        {
            double pk = Math.Clamp(_peakLoad / 100.0, 0, 1);
            LoadPeakTick.Margin = new Thickness(Math.Max(0, pk * w - 1), 1, 0, 1);
            LoadPeakTick.Visibility = Visibility.Visible;
        }
        else
        {
            LoadPeakTick.Visibility = Visibility.Collapsed;
        }

        if (!double.IsNaN(_loadValue))
        {
            double lf = Math.Clamp(_loadValue / 100.0, 0, 1);
            LoadTri.Margin = new Thickness(Math.Max(0, 1 + lf * w - 4.5), 4, 0, 0);
            LoadTri.Visibility = Visibility.Visible;
        }
        else
        {
            LoadTri.Visibility = Visibility.Collapsed;
        }
    }

    // The clip geometry is reused and resized in place, never reallocated.
    private readonly System.Windows.Media.RectangleGeometry _clip = new();

    private Color ZoneColor(double v, bool has)
    {
        if (!has) return C("#AEB6C6");
        if (!double.IsNaN(RedFrom) && v >= RedFrom) return C("#F85149");
        if (!double.IsNaN(GreenTo) && v <= GreenTo) return C("#3FB950");
        if (!double.IsNaN(GreenTo) || !double.IsNaN(RedFrom)) return C("#E3B341");
        return C("#AEB6C6"); // zoneless (fan %): neutral steel, same as the needle metal
    }

    // ---- shared frozen paint: one instance per zone colour, app-wide.
    // Frozen Freezables are thread-safe, shareable, and never re-created, so a
    // day in the tray allocates zero paint. Four zone colours = tiny caches.

    private static readonly Dictionary<Color, SolidColorBrush> SolidCache = new();
    private static readonly Dictionary<Color, LinearGradientBrush> FillCache = new();
    private static readonly Dictionary<Color, DropShadowEffect> GlowCache = new();

    private static SolidColorBrush Solid(Color c)
    {
        if (!SolidCache.TryGetValue(c, out SolidColorBrush? b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            SolidCache[c] = b;
        }
        return b;
    }

    private static LinearGradientBrush FillFor(Color c)
    {
        if (!FillCache.TryGetValue(c, out LinearGradientBrush? b))
        {
            // Lit from the top like everything else on the dash.
            b = new LinearGradientBrush(
                Color.FromRgb((byte)Math.Min(255, c.R + 40), (byte)Math.Min(255, c.G + 40), (byte)Math.Min(255, c.B + 40)),
                Color.FromRgb((byte)(c.R * 0.55), (byte)(c.G * 0.55), (byte)(c.B * 0.55)),
                90);
            b.Freeze();
            FillCache[c] = b;
        }
        return b;
    }

    private static DropShadowEffect GlowFor(Color c)
    {
        if (!GlowCache.TryGetValue(c, out DropShadowEffect? e))
        {
            e = new DropShadowEffect { Color = c, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.45 };
            e.Freeze();
            GlowCache[c] = e;
        }
        return e;
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
}
