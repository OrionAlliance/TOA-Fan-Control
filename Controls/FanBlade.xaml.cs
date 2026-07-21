using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FanControlApp.Controls;

/// <summary>
/// A case fan drawn as the real thing: a square frame with corner screws, a dished
/// circular recess, and a set of blades that actually spin - faster the higher the
/// RPM, stopped when it's stopped. The reading sits in the hub. This is the fan
/// counterpart to <see cref="Gauge"/>: temps get a dial, fans get a fan.
/// </summary>
public partial class FanBlade : UserControl
{
    // How many blades. Seven reads unmistakably as a computer case fan.
    private const int BladeCount = 7;

    // How much of the height is set aside under the fan for the name.
    private const double LabelBand = 24;

    private RotateTransform? _spin;
    private TextBlock? _hubText;

    private double _cx, _cy, _r;

    public FanBlade()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Rebuild();
    }

    // ---- properties ---------------------------------------------------------

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(FanBlade),
            new PropertyMetadata("", (d, _) => ((FanBlade)d).Rebuild()));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(FanBlade),
            new PropertyMetadata(double.NaN, (d, _) => ((FanBlade)d).UpdateSpin()));

    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(double), typeof(FanBlade),
            new PropertyMetadata(double.NaN, (d, _) => ((FanBlade)d).UpdateReadout()));

    /// <summary>Fan name, shown under the frame. May be two lines ("Chassis\nFan #2").</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>Current RPM. Drives the spin speed - the blades move at the real rate.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>The duty the app is driving this fan at. Shown in the hub.</summary>
    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }

    // ---- geometry -----------------------------------------------------------

    private Point PointAt(double angleDeg, double radius)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new Point(_cx + radius * Math.Cos(rad), _cy + radius * Math.Sin(rad));
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
    private static Brush B(string hex) => new SolidColorBrush(C(hex));

    // ---- build --------------------------------------------------------------

    private void Rebuild()
    {
        FrameLayer.Children.Clear();
        BladeLayer.Children.Clear();
        HubLayer.Children.Clear();
        _spin = null;
        _hubText = null;

        if (ActualWidth <= 20 || ActualHeight <= 20) return;

        // The fan sits in the space above the label band, centred left-to-right.
        double boxH = ActualHeight - LabelBand;
        _cx = ActualWidth / 2;
        _cy = boxH / 2;
        _r = Math.Min(ActualWidth, boxH) / 2 - 6;

        DrawFrame();
        DrawBlades();
        DrawHub();
        DrawLabel();

        UpdateReadout();
        UpdateSpin();
    }

    /// <summary>The square housing: rounded metal frame, four screws, a dished bore.</summary>
    private void DrawFrame()
    {
        double side = _r * 2 + 10;
        double left = _cx - side / 2;
        double top = _cy - side / 2;

        // Frame - brushed metal, lit from the top-left like the dials.
        var frame = new Rectangle
        {
            Width = side,
            Height = side,
            RadiusX = side * 0.14,
            RadiusY = side * 0.14,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0.1, 0),
                EndPoint = new Point(0.9, 1),
                GradientStops =
                {
                    new GradientStop(C("#3C4356"), 0),
                    new GradientStop(C("#242835"), 0.5),
                    new GradientStop(C("#171A23"), 1),
                },
            },
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 14, ShadowDepth = 4,
                Direction = 270, Opacity = 0.5,
            },
        };
        Canvas.SetLeft(frame, left);
        Canvas.SetTop(frame, top);
        FrameLayer.Children.Add(frame);

        // Corner screws, pulled in from the frame edge.
        double inset = side * 0.13;
        foreach (var (dx, dy) in new[] { (1, 1), (-1, 1), (1, -1), (-1, -1) })
        {
            double sx = _cx + dx * (side / 2 - inset);
            double sy = _cy + dy * (side / 2 - inset);
            var screw = new Ellipse
            {
                Width = 7, Height = 7,
                Fill = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.35, 0.3),
                    GradientStops =
                    {
                        new GradientStop(C("#4B5266"), 0),
                        new GradientStop(C("#14171F"), 1),
                    },
                },
            };
            Canvas.SetLeft(screw, sx - 3.5);
            Canvas.SetTop(screw, sy - 3.5);
            FrameLayer.Children.Add(screw);
        }

        // The bore: a round recess the blades sit inside.
        var bore = new Ellipse
        {
            Width = _r * 2,
            Height = _r * 2,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.38, 0.32),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.6, RadiusY = 0.6,
                GradientStops =
                {
                    new GradientStop(C("#20242F"), 0),
                    new GradientStop(C("#0C0E13"), 1),
                },
            },
        };
        Canvas.SetLeft(bore, _cx - _r);
        Canvas.SetTop(bore, _cy - _r);
        FrameLayer.Children.Add(bore);
    }

    /// <summary>The blades, arranged round the hub. The whole layer spins as one.</summary>
    private void DrawBlades()
    {
        double rInner = _r * 0.30;
        double rOuter = _r * 0.95;
        Geometry blade = BladeGeometry(rInner, rOuter);

        for (int k = 0; k < BladeCount; k++)
        {
            var p = new Path
            {
                Data = blade,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0xE8, 0xC3, 0xCB, 0xDB), 0),
                        new GradientStop(Color.FromArgb(0xE0, 0x8A, 0x93, 0xA8), 0.55),
                        new GradientStop(Color.FromArgb(0xD8, 0x51, 0x59, 0x6E), 1),
                    },
                },
                Stroke = B("#141720"),
                StrokeThickness = 1,
                RenderTransform = new RotateTransform(k * 360.0 / BladeCount, _cx, _cy),
            };
            BladeLayer.Children.Add(p);
        }

        // Spin the layer as a whole about the hub.
        _spin = new RotateTransform(0, _cx, _cy);
        BladeLayer.RenderTransform = _spin;
    }

    /// <summary>One blade, pointing east (angle 0), swept and pitched like a real one.</summary>
    private Geometry BladeGeometry(double rInner, double rOuter)
    {
        const double halfInner = 9;   // angular half-width where it meets the hub
        const double halfOuter = 20;  // ...and at the tip
        const double pitch = 16;      // lean, so the set reads as a pinwheel
        double rMid = (rInner + rOuter) / 2;

        Point a = PointAt(-halfInner, rInner);
        Point lead = PointAt(-halfOuter + pitch, rOuter);
        Point trail = PointAt(halfOuter + pitch, rOuter);
        Point b = PointAt(halfInner, rInner);

        // Bowed leading/trailing edges give the blade an airfoil curve rather than
        // a flat paddle.
        Point leadCtrl = PointAt(-halfOuter + pitch - 8, rMid + 6);
        Point trailCtrl = PointAt(halfInner + pitch + 6, rMid - 4);

        var fig = new PathFigure { StartPoint = a, IsClosed = true };
        fig.Segments.Add(new QuadraticBezierSegment(leadCtrl, lead, true));
        fig.Segments.Add(new ArcSegment(trail, new Size(rOuter, rOuter), 0, false,
            SweepDirection.Clockwise, true));
        fig.Segments.Add(new QuadraticBezierSegment(trailCtrl, b, true));
        fig.Segments.Add(new ArcSegment(a, new Size(rInner, rInner), 0, false,
            SweepDirection.Counterclockwise, true));

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    /// <summary>Raised centre cap; the fan % rides on top of it (and doesn't spin).</summary>
    private void DrawHub()
    {
        double hubR = Math.Max(20, _r * 0.34);

        var hub = new Ellipse
        {
            Width = hubR * 2,
            Height = hubR * 2,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.35, 0.3),
                GradientStops =
                {
                    new GradientStop(C("#5A6478"), 0),
                    new GradientStop(C("#2A3040"), 0.6),
                    new GradientStop(C("#12151D"), 1),
                },
            },
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 8, ShadowDepth = 2.5,
                Direction = 300, Opacity = 0.7,
            },
        };
        Canvas.SetLeft(hub, _cx - hubR);
        Canvas.SetTop(hub, _cy - hubR);
        HubLayer.Children.Add(hub);

        _hubText = new TextBlock
        {
            Foreground = B("#FFFFFF"),
            FontSize = Math.Max(13, hubR * 0.56),
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Width = hubR * 2,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1,
                Direction = 270, Opacity = 0.9,
            },
        };
        _hubText.Measure(new Size(hubR * 2, double.PositiveInfinity));
        Canvas.SetLeft(_hubText, _cx - hubR);
        Canvas.SetTop(_hubText, _cy - _hubText.DesiredSize.Height / 2);
        HubLayer.Children.Add(_hubText);
    }

    /// <summary>Fan name, on one line under the frame.</summary>
    private void DrawLabel()
    {
        var lab = new TextBlock
        {
            Text = Label,
            Foreground = B("#FFFFFF"),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            Width = ActualWidth,
        };
        Canvas.SetLeft(lab, 0);
        Canvas.SetTop(lab, ActualHeight - LabelBand + 4);
        FrameLayer.Children.Add(lab);
    }

    // ---- live updates -------------------------------------------------------

    private void UpdateReadout()
    {
        if (_hubText == null) return;
        double p = Percent;
        _hubText.Text = double.IsNaN(p)
            ? "--"
            : p.ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// Map RPM to a pleasing spin - not literal (a real fan at 1500 RPM would be an
    /// unreadable blur), just "faster when it's working harder". Restart from the
    /// current angle so a speed change never snaps the blades back to zero.
    /// </summary>
    private void UpdateSpin()
    {
        if (_spin == null) return;

        double rpm = Value;
        bool spinning = !double.IsNaN(rpm) && rpm > 0;

        if (!spinning)
        {
            double held = _spin.Angle;
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
            _spin.Angle = held;
            return;
        }

        double revsPerSec = 0.35 + Math.Clamp(rpm, 0, 2000) / 2000.0 * 2.4;
        double from = _spin.Angle;

        _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            From = from,
            To = from + 360,
            Duration = TimeSpan.FromSeconds(1.0 / revsPerSec),
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }
}
