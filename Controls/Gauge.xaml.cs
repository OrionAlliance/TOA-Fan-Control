using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FanControlApp.Controls;

/// <summary>Car-dash dial with needle, optional green/red bands, a load lane and session peak marks.</summary>
public partial class Gauge : UserControl
{
    // Sweep 135 to 405 deg through the top; screen angles, so 270 is straight up.
    private const double StartAngle = 135;
    private const double SweepAngle = 270;
    private const double EndAngle = StartAngle + SweepAngle;

    private Polygon? _needle;
    private RotateTransform? _needleRotate;
    private Path? _peakMark;
    private Path? _peakHit;
    private RotateTransform? _peakRotate;
    private Polygon? _loadMark;
    private Polygon? _loadHit;
    private RotateTransform? _loadRotate;
    private Path? _loadPeakMark;
    private Path? _loadPeakHit;
    private RotateTransform? _loadPeakRotate;
    private TextBlock? _valueText;

    private double _cx, _cy, _r;

    // Radii derived from the bezel so the dial scales.
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

    // Wraps sentence-length band tooltips into a readable block.
    private static ToolTip Tip(string text) => new()
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 },
    };

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

        // Drop old parts so an early return leaves no orphans for UpdateMoving to animate.
        _needle = null;
        _needleRotate = null;
        _peakMark = null;
        _peakHit = null;
        _peakRotate = null;
        _loadMark = null;
        _loadHit = null;
        _loadRotate = null;
        _loadPeakMark = null;
        _loadPeakHit = null;
        _loadPeakRotate = null;
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
        UpdatePeakMarks();
        UpdateLiveLoad();
    }
}
