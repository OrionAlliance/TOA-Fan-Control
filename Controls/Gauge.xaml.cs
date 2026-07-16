using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace FanControlApp.Controls;

/// <summary>
/// A car-dash gauge: swept dial, white needle, an optional green safe band and
/// red danger band, and a yellow mark that sticks at the highest value seen so
/// far this run.
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
    private RotateTransform? _peakRotate;
    private TextBlock? _valueText;

    private double _cx, _cy, _r;

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

    /// <summary>Highest value seen since the last reset. NaN until the first reading.</summary>
    public double Peak { get; private set; } = double.NaN;

    private static DependencyProperty Reg(string name, object def) =>
        DependencyProperty.Register(name, def.GetType(), typeof(Gauge),
            new PropertyMetadata(def, (d, _) => ((Gauge)d).Rebuild()));

    public void ResetPeak()
    {
        Peak = double.NaN;
        UpdateMoving();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (Gauge)d;
        var v = (double)e.NewValue;

        if (!double.IsNaN(v) && (double.IsNaN(g.Peak) || v > g.Peak))
            g.Peak = v;

        g.UpdateMoving();
    }

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

    private static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

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
        _needle = null;

        if (ActualWidth <= 20 || ActualHeight <= 20) return;

        _cx = ActualWidth / 2;
        _cy = ActualHeight / 2;
        _r = Math.Min(ActualWidth, ActualHeight) / 2 - 6;

        DrawFace();
        BuildMoving();
        UpdateMoving();
    }

    private void DrawFace()
    {
        double bandR = _r - 5;

        // Base sweep
        Face.Children.Add(Arc(StartAngle, EndAngle, bandR, B("#2A2F3D"), 7));

        // Green safe band
        if (!double.IsNaN(GreenTo) && GreenTo > Minimum)
            Face.Children.Add(Arc(StartAngle, AngleFor(GreenTo), bandR, B("#3FB950"), 7));

        // Red danger band
        if (!double.IsNaN(RedFrom) && RedFrom < Maximum)
            Face.Children.Add(Arc(AngleFor(RedFrom), EndAngle, bandR, B("#F85149"), 7));

        // The redline itself - a hard mark at where trouble starts
        if (!double.IsNaN(RedFrom))
        {
            double a = AngleFor(RedFrom);
            Face.Children.Add(new Line
            {
                X1 = PointAt(a, _r - 14).X, Y1 = PointAt(a, _r - 14).Y,
                X2 = PointAt(a, _r + 1).X, Y2 = PointAt(a, _r + 1).Y,
                Stroke = B("#F85149"), StrokeThickness = 2.5,
            });
        }

        DrawTicks();
        DrawText();
    }

    private void DrawTicks()
    {
        if (MajorTick <= 0) return;

        Brush tick = B("#8A92A6");
        Brush num = B("#E6E9F0");
        double minor = MajorTick / 2;

        for (double v = Minimum; v <= Maximum + 0.0001; v += minor)
        {
            double a = AngleFor(v);
            bool isMajor = Math.Abs(v / MajorTick - Math.Round(v / MajorTick)) < 0.001;

            Point p1 = PointAt(a, _r - (isMajor ? 14 : 10));
            Point p2 = PointAt(a, _r - 6);

            Face.Children.Add(new Line
            {
                X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                Stroke = tick,
                StrokeThickness = isMajor ? 2 : 1,
                Opacity = isMajor ? 1 : 0.6,
            });

            if (!isMajor) continue;

            // Numbers get unreadable on a small dial, so shorten thousands (1500 -> 1.5k)
            string text = Maximum >= 1000
                ? (v / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k"
                : v.ToString("0", CultureInfo.InvariantCulture);

            var tb = new TextBlock { Text = text, Foreground = num, FontSize = 9 };
            tb.Measure(new Size(100, 100));
            Point np = PointAt(a, _r - 26);
            Canvas.SetLeft(tb, np.X - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, np.Y - tb.DesiredSize.Height / 2);
            Face.Children.Add(tb);
        }
    }

    private void DrawText()
    {
        _valueText = new TextBlock
        {
            Foreground = B("#E6E9F0"),
            FontSize = Math.Max(15, _r * 0.26),
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Width = _r * 1.6,
        };
        Canvas.SetLeft(_valueText, _cx - _r * 0.8);
        Canvas.SetTop(_valueText, _cy + _r * 0.14);
        Face.Children.Add(_valueText);

        var lab = new TextBlock
        {
            Text = Label,
            Foreground = B("#8A92A6"),
            FontSize = 10,
            TextAlignment = TextAlignment.Center,
            Width = _r * 1.6,
        };
        Canvas.SetLeft(lab, _cx - _r * 0.8);
        Canvas.SetTop(lab, _cy + _r * 0.14 + Math.Max(15, _r * 0.26) + 2);
        Face.Children.Add(lab);
    }

    // ---- needle + peak ------------------------------------------------------

    private void BuildMoving()
    {
        double len = _r - 16;

        // Yellow peak mark - drawn under the needle so the needle wins on overlap.
        _peakRotate = new RotateTransform(StartAngle);
        var peakFig = new PathFigure { StartPoint = new Point(len - 12, 0) };
        peakFig.Segments.Add(new LineSegment(new Point(len + 4, 0), true));
        var peakGeo = new PathGeometry();
        peakGeo.Figures.Add(peakFig);

        _peakMark = new Path
        {
            Data = peakGeo,
            Stroke = B("#E3B341"),
            StrokeThickness = 3,
            RenderTransform = new TransformGroup
            {
                Children = { _peakRotate, new TranslateTransform(_cx, _cy) },
            },
        };
        Moving.Children.Add(_peakMark);

        // White needle
        _needleRotate = new RotateTransform(StartAngle);
        _needle = new Polygon
        {
            Fill = B("#FFFFFF"),
            Points = new PointCollection
            {
                new Point(len, 0),
                new Point(0, -3.2),
                new Point(-10, 0),
                new Point(0, 3.2),
            },
            RenderTransform = new TransformGroup
            {
                Children = { _needleRotate, new TranslateTransform(_cx, _cy) },
            },
        };
        Moving.Children.Add(_needle);

        var hub = new Ellipse
        {
            Width = 11, Height = 11,
            Fill = B("#1A1D26"),
            Stroke = B("#8A92A6"),
            StrokeThickness = 1.5,
        };
        Canvas.SetLeft(hub, _cx - 5.5);
        Canvas.SetTop(hub, _cy - 5.5);
        Moving.Children.Add(hub);
    }

    private void UpdateMoving()
    {
        if (_needleRotate == null || _valueText == null) return;

        double v = Value;
        bool has = !double.IsNaN(v);

        _valueText.Text = has
            ? v.ToString(Maximum >= 1000 ? "0" : "0", CultureInfo.InvariantCulture) +
              (string.IsNullOrEmpty(Unit) ? "" : " " + Unit)
            : "--";

        _valueText.Foreground = !has ? B("#8A92A6")
            : !double.IsNaN(RedFrom) && v >= RedFrom ? B("#F85149")
            : !double.IsNaN(GreenTo) && v <= GreenTo ? B("#3FB950")
            : B("#E6E9F0");

        // Animate rather than snap - a needle that jumps once a second reads as
        // broken; one that sweeps reads as a gauge.
        double target = AngleFor(has ? v : Minimum);
        _needleRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

        if (_peakMark == null || _peakRotate == null) return;

        bool hasPeak = !double.IsNaN(Peak);
        _peakMark.Visibility = hasPeak ? Visibility.Visible : Visibility.Collapsed;
        if (hasPeak)
            _peakRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
            {
                To = AngleFor(Peak),
                Duration = TimeSpan.FromMilliseconds(350),
            });
    }
}
