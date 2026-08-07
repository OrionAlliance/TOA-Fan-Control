using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FanControlApp.Controls;

/// <summary>
/// A car-dash gauge: machined bezel, dished face, glass, a white needle floating
/// above it, an optional green safe band and red danger band, and a yellow mark
/// that sticks at the highest value seen so far this run.
/// </summary>
public partial class Gauge : UserControl
{
    // Classic car sweep: 135 deg (bottom-left) round through the top to 405 deg
    // (bottom-right). Screen angles, so 270 is straight up.
    private const double StartAngle = 135;
    private const double SweepAngle = 270;
    private const double EndAngle = StartAngle + SweepAngle;

    private Polygon? _needle;
    private RotateTransform? _needleRotate;
    private Path? _peakMark;
    private Path? _peakHit;
    private RotateTransform? _peakRotate;
    private TextBlock? _valueText;

    private double _cx, _cy, _r;

    // Radii, outside in. Everything is derived from the bezel so the dial scales.
    private double FaceR => _r - 5;
    private double BandR => _r - 13;
    private double TickOuter => _r - 14;
    private double NumberR => _r - 34;
    private double NeedleLen => _r - 24;

    public Gauge()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Rebuild();
    }

    // ---- properties ---------------------------------------------------------

    public static readonly DependencyProperty LabelProperty = Reg(nameof(Label), "");
    public static readonly DependencyProperty UnitProperty = Reg(nameof(Unit), "");
    public static readonly DependencyProperty MinimumProperty = Reg(nameof(Minimum), 0d);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), 100d);
    public static readonly DependencyProperty MajorTickProperty = Reg(nameof(MajorTick), 20d);

    /// <summary>Green runs from Minimum to here. NaN = no green band.</summary>
    public static readonly DependencyProperty GreenToProperty = Reg(nameof(GreenTo), double.NaN);

    /// <summary>Red runs from here to Maximum. NaN = no red band.</summary>
    public static readonly DependencyProperty RedFromProperty = Reg(nameof(RedFrom), double.NaN);

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(Gauge),
            new PropertyMetadata(double.NaN, OnValueChanged));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double MajorTick { get => (double)GetValue(MajorTickProperty); set => SetValue(MajorTickProperty, value); }
    public double GreenTo { get => (double)GetValue(GreenToProperty); set => SetValue(GreenToProperty, value); }
    public double RedFrom { get => (double)GetValue(RedFromProperty); set => SetValue(RedFromProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private double _peak = double.NaN;
    private double _peakLoad = double.NaN;

    /// <summary>Load % the moment the peak was set - the peak tooltip's context. Set before Peak.</summary>
    public double PeakLoad { set => _peakLoad = value; }

    /// <summary>
    /// The session peak, fed by the controller - one truth shared by every view
    /// (dials, bars, Game Mode), so switching views can never disagree. NaN hides
    /// the marker.
    /// </summary>
    public double Peak
    {
        get => _peak;
        set
        {
            if (value.Equals(_peak) || (double.IsNaN(value) && double.IsNaN(_peak))) return;
            _peak = value;
            UpdateMoving();
        }
    }

    private static DependencyProperty Reg(string name, object def) =>
        DependencyProperty.Register(name, def.GetType(), typeof(Gauge),
            new PropertyMetadata(def, (d, _) => ((Gauge)d).Rebuild()));

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Gauge)d).UpdateMoving();

    // ---- geometry -----------------------------------------------------------

    private double AngleFor(double value)
    {
        double span = Maximum - Minimum;
        if (span <= 0) return StartAngle;
        double t = Math.Clamp((value - Minimum) / span, 0, 1);
        return StartAngle + t * SweepAngle;
    }

    private Point PointAt(double angleDeg, double radius)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new Point(_cx + radius * Math.Cos(rad), _cy + radius * Math.Sin(rad));
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
    private static Brush B(string hex) => new SolidColorBrush(C(hex));

    private void PlaceCentered(FrameworkElement e, double radius)
    {
        e.Width = radius * 2;
        e.Height = radius * 2;
        Canvas.SetLeft(e, _cx - radius);
        Canvas.SetTop(e, _cy - radius);
    }

    private Path Arc(double a0, double a1, double radius, Brush stroke, double thickness)
    {
        var fig = new PathFigure { StartPoint = PointAt(a0, radius) };
        fig.Segments.Add(new ArcSegment
        {
            Point = PointAt(a1, radius),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = a1 - a0 > 180,
        });

        var geo = new PathGeometry();
        geo.Figures.Add(fig);

        return new Path
        {
            Data = geo,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
        };
    }

    // ---- face ---------------------------------------------------------------

    private void Rebuild()
    {
        Face.Children.Clear();
        Moving.Children.Clear();

        // Drop the old parts too - if we bail out below, UpdateMoving must not
        // animate orphans that are no longer in the tree.
        _needle = null;
        _needleRotate = null;
        _peakMark = null;
        _peakHit = null;
        _peakRotate = null;
        _valueText = null;

        if (ActualWidth <= 20 || ActualHeight <= 20) return;

        _cx = ActualWidth / 2;
        _cy = ActualHeight / 2;
        _r = Math.Min(ActualWidth, ActualHeight) / 2 - 6;

        DrawBezel();
        DrawBands();
        DrawTicks();
        DrawGloss();
        DrawText();
        BuildMoving();
        UpdateMoving();
    }

    /// <summary>Machined ring, dished face, and the shadow the rim casts inward.</summary>
    private void DrawBezel()
    {
        // Lit from the top-left, like everything else on the dial.
        var bezel = new Ellipse
        {
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0.15, 0),
                EndPoint = new Point(0.85, 1),
                GradientStops =
                {
                    new GradientStop(C("#4A5266"), 0),
                    new GradientStop(C("#232735"), 0.45),
                    new GradientStop(C("#171A23"), 0.7),
                    new GradientStop(C("#39405044"), 1),
                },
            },
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 14,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.55,
            },
        };
        PlaceCentered(bezel, _r);
        Face.Children.Add(bezel);

        // Dished face: light pools toward the upper-left, falls away to the rim.
        var face = new Ellipse
        {
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.36, 0.3),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.78,
                RadiusY = 0.78,
                GradientStops =
                {
                    new GradientStop(C("#2B3140"), 0),
                    new GradientStop(C("#191D27"), 0.6),
                    new GradientStop(C("#0C0E13"), 1),
                },
            },
        };
        PlaceCentered(face, FaceR);
        Face.Children.Add(face);

        // Inner shadow - sells the idea that the face sits below the rim.
        var innerShadow = new Ellipse
        {
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0.72),
                    new GradientStop(Color.FromArgb(90, 0, 0, 0), 0.93),
                    new GradientStop(Color.FromArgb(150, 0, 0, 0), 1),
                },
            },
            IsHitTestVisible = false,
        };
        PlaceCentered(innerShadow, FaceR);
        Face.Children.Add(innerShadow);
    }

    private void DrawBands()
    {
        // Base sweep, recessed
        Face.Children.Add(Arc(StartAngle, EndAngle, BandR, B("#323848"), 7));

        if (!double.IsNaN(GreenTo) && GreenTo > Minimum)
        {
            Path green = Arc(StartAngle, AngleFor(GreenTo), BandR, B("#3FB950"), 7);
            green.Effect = new DropShadowEffect
            {
                Color = C("#3FB950"), BlurRadius = 9, ShadowDepth = 0, Opacity = 0.5,
            };
            Face.Children.Add(green);
        }

        if (!double.IsNaN(RedFrom) && RedFrom < Maximum)
        {
            Path red = Arc(AngleFor(RedFrom), EndAngle, BandR, B("#F85149"), 7);
            red.Effect = new DropShadowEffect
            {
                Color = C("#F85149"), BlurRadius = 9, ShadowDepth = 0, Opacity = 0.55,
            };
            Face.Children.Add(red);
        }

        // The redline itself - a hard mark at where trouble starts
        if (double.IsNaN(RedFrom)) return;

        double a = AngleFor(RedFrom);
        Face.Children.Add(new Line
        {
            X1 = PointAt(a, BandR - 9).X, Y1 = PointAt(a, BandR - 9).Y,
            X2 = PointAt(a, BandR + 6).X, Y2 = PointAt(a, BandR + 6).Y,
            Stroke = B("#F85149"),
            StrokeThickness = 2.5,
        });
    }

    private void DrawTicks()
    {
        if (MajorTick <= 0) return;

        Brush tick = B("#9AA3B8");
        Brush num = B("#FFFFFF");

        // Every NUMBERED value (each 10 on the temp dials) gets the long heavy
        // marker - a number deserves a real tick. The short faint ticks sit on
        // the unnumbered midpoints between them (5, 15, 25, ...).
        double numberedStep = MajorTick / 2;
        double step = MajorTick / 4;

        for (double v = Minimum; v <= Maximum + 0.0001; v += step)
        {
            double a = AngleFor(v);
            bool numbered = Math.Abs(v / numberedStep - Math.Round(v / numberedStep)) < 0.001;

            Point p1 = PointAt(a, TickOuter - (numbered ? 9 : 5));
            Point p2 = PointAt(a, TickOuter);

            Face.Children.Add(new Line
            {
                X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                Stroke = tick,
                StrokeThickness = numbered ? 2 : 1,
                Opacity = numbered ? 1 : 0.55,
            });

            if (!numbered) continue; // midpoints are markers only, no label

            string text = Maximum >= 1000
                ? (v / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k"
                : v.ToString("0", CultureInfo.InvariantCulture);

            var tb = new TextBlock { Text = text, Foreground = num, FontSize = 9 };
            tb.Measure(new Size(100, 100));
            Point np = PointAt(a, NumberR);
            Canvas.SetLeft(tb, np.X - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, np.Y - tb.DesiredSize.Height / 2);
            Face.Children.Add(tb);
        }
    }

    /// <summary>The glass: a soft highlight across the upper face, clipped to the dial.</summary>
    private void DrawGloss()
    {
        double gw = FaceR * 1.75;
        double gh = FaceR * 1.15;
        double left = _cx - gw / 2;
        double top = _cy - FaceR * 1.02;

        var gloss = new Ellipse
        {
            Width = gw,
            Height = gh,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(30, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(12, 255, 255, 255), 0.55),
                    new GradientStop(Colors.Transparent, 1),
                },
            },
            // Clip in the gloss's own coordinate space, so it can't spill past the rim.
            Clip = new EllipseGeometry(new Point(_cx - left, _cy - top), FaceR, FaceR),
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(gloss, left);
        Canvas.SetTop(gloss, top);
        Face.Children.Add(gloss);
    }

    private void DrawText()
    {
        double size = Math.Max(15, _r * 0.26);

        _valueText = new TextBlock
        {
            Foreground = B("#FFFFFF"),
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Width = _r * 1.6,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1.5,
                Direction = 270, Opacity = 0.8,
            },
        };
        Canvas.SetLeft(_valueText, _cx - _r * 0.8);
        Canvas.SetTop(_valueText, _cy + _r * 0.16);
        Face.Children.Add(_valueText);

        var lab = new TextBlock
        {
            Text = Label,
            Foreground = B("#FFFFFF"),
            FontSize = 10,
            TextAlignment = TextAlignment.Center,
            Width = _r * 1.6,
            LineHeight = 12,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        Canvas.SetLeft(lab, _cx - _r * 0.8);

        // Sit the label's BOTTOM on a fixed baseline low in the dial's dark gap,
        // rather than its top. Anchoring the top makes a one-line "CPU" and a
        // two-line "Chassis / Fan #2" finish at different heights, so the row
        // doesn't line up. This way every label ends on the same line whatever
        // its height.
        //
        // 0.86r puts a two-line label wholly inside the gap: the band's ends stop
        // at ~0.58r vertically, so anything above that straddles them instead of
        // sitting in the dark. It's also near the floor - the circle narrows fast
        // down here and the text soon runs out of dial to sit on.
        lab.Measure(new Size(_r * 1.6, double.PositiveInfinity));
        Canvas.SetTop(lab, _cy + _r * 0.86 - lab.DesiredSize.Height);
        Face.Children.Add(lab);
    }

    // ---- needle + peak ------------------------------------------------------

    private void BuildMoving()
    {
        double len = NeedleLen;

        // Yellow peak mark - drawn under the needle so the needle wins on overlap.
        _peakRotate = new RotateTransform(StartAngle);
        var peakFig = new PathFigure { StartPoint = new Point(BandR - 11, 0) };
        peakFig.Segments.Add(new LineSegment(new Point(BandR + 6, 0), true));
        var peakGeo = new PathGeometry();
        peakGeo.Figures.Add(peakFig);

        // Shared, so the mark and its hit area can never drift apart.
        var peakTransform = new TransformGroup
        {
            Children = { _peakRotate, new TranslateTransform(_cx, _cy) },
        };

        // A fat invisible copy of the mark, purely to catch the mouse. The visible
        // line is 3px on a rotated dial - nobody is landing on that. Transparent
        // still hit-tests; null wouldn't.
        _peakHit = new Path
        {
            Data = peakGeo,
            Stroke = Brushes.Transparent,
            StrokeThickness = 18,
            RenderTransform = peakTransform,
            Cursor = Cursors.Hand,
        };
        Moving.Children.Add(_peakHit);

        _peakMark = new Path
        {
            Data = peakGeo,
            Stroke = B("#E3B341"),
            StrokeThickness = 3,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = C("#E3B341"), BlurRadius = 7, ShadowDepth = 0, Opacity = 0.85,
            },
            RenderTransform = peakTransform,
        };
        Moving.Children.Add(_peakMark);

        // White needle. The gradient across its width reads as a rounded edge;
        // the shadow lifts it off the face.
        _needleRotate = new RotateTransform(StartAngle);
        _needle = new Polygon
        {
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(C("#FFFFFF"), 0),
                    new GradientStop(C("#F2F5FA"), 0.5),
                    new GradientStop(C("#AEB6C6"), 1),
                },
            },
            Points = new PointCollection
            {
                new Point(len, 0),
                new Point(0, -3.4),
                new Point(-11, 0),
                new Point(0, 3.4),
            },
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 9, ShadowDepth = 3.5,
                Direction = 300, Opacity = 0.65,
            },
            RenderTransform = new TransformGroup
            {
                Children = { _needleRotate, new TranslateTransform(_cx, _cy) },
            },
        };
        Moving.Children.Add(_needle);

        // Raised hub, capping the needle's pivot.
        var hub = new Ellipse
        {
            Width = 15,
            Height = 15,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.35, 0.3),
                GradientStops =
                {
                    new GradientStop(C("#5A6478"), 0),
                    new GradientStop(C("#2A3040"), 0.65),
                    new GradientStop(C("#14171F"), 1),
                },
            },
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 6, ShadowDepth = 2,
                Direction = 300, Opacity = 0.7,
            },
        };
        Canvas.SetLeft(hub, _cx - 7.5);
        Canvas.SetTop(hub, _cy - 7.5);
        Moving.Children.Add(hub);
    }

    private void UpdateMoving()
    {
        if (_needleRotate == null || _valueText == null) return;

        double v = Value;
        bool has = !double.IsNaN(v);

        _valueText.Text = has
            ? v.ToString("0", CultureInfo.InvariantCulture) +
              (string.IsNullOrEmpty(Unit) ? "" : " " + Unit)
            : "--";

        _valueText.Foreground = !has ? B("#FFFFFF")
            : !double.IsNaN(RedFrom) && v >= RedFrom ? B("#F85149")
            : !double.IsNaN(GreenTo) && v <= GreenTo ? B("#3FB950")
            : B("#FFFFFF");

        // Animate rather than snap - a needle that jumps once a second reads as
        // broken; one that sweeps reads as a gauge.
        double target = AngleFor(has ? v : Minimum);
        _needleRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

        if (_peakMark == null || _peakRotate == null || _peakHit == null) return;

        bool hasPeak = !double.IsNaN(Peak);
        Visibility peakVis = hasPeak ? Visibility.Visible : Visibility.Collapsed;
        _peakMark.Visibility = peakVis;
        _peakHit.Visibility = peakVis;

        if (!hasPeak) return;

        string unit = string.IsNullOrEmpty(Unit) ? "" : " " + Unit;

        // Labels may be stacked on the dial ("Chassis\nFan #2"); flatten it here or
        // the tooltip breaks across two lines mid-sentence.
        string flat = Label.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        _peakHit.ToolTip = double.IsNaN(_peakLoad)
            ? $"{flat} peak this run: {Peak:0}{unit}"
            : $"{flat} peak this run: {Peak:0}{unit} ({_peakLoad:0}% load)";

        _peakRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            To = AngleFor(Peak),
            Duration = TimeSpan.FromMilliseconds(350),
        });
    }
}
