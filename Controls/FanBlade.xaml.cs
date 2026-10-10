using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FanControlApp.Controls;

/// <summary>A drawn case fan whose blades spin faster with RPM; the fan counterpart to <see cref="Gauge"/>.</summary>
public partial class FanBlade : UserControl
{
    // Seven reads unmistakably as a case fan.
    private const int BladeCount = 7;

    // Height reserved under the fan for the name.
    private const double LabelBand = 24;

    private RotateTransform? _spin;
    private TextBlock? _hubText;

    private double _cx, _cy, _r;

    public FanBlade()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Rebuild();
        IsVisibleChanged += (_, _) => UpdateSpin(); // a running animation redraws 60 fps even when hidden
    }

    private double _revs; // speed of the running spin, 0 = stopped
    private bool _hold;

    /// <summary>Freeze the blades (e.g. window minimized); IsVisible doesn't cover that.</summary>
    public bool Hold
    {
        get => _hold;
        set { _hold = value; UpdateSpin(); }
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

    /// <summary>Fan name, shown under the frame.</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>Current RPM; drives the spin speed.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>Duty the app is driving this fan at, shown in the hub.</summary>
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

        // Fan is centred above the label band.
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

    // ---- live updates -------------------------------------------------------

    private void UpdateReadout()
    {
        if (_hubText == null) return;
        double p = Percent;
        _hubText.Text = double.IsNaN(p)
            ? "--"
            : p.ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>Maps RPM to a readable (not literal) spin, restarting from the current angle so it never snaps.</summary>
    private void UpdateSpin()
    {
        if (_spin == null) return;

        double rpm = Value;
        bool spinning = !double.IsNaN(rpm) && rpm > 0 && IsVisible && !_hold;

        if (!spinning)
        {
            double held = _spin.Angle;
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
            _spin.Angle = held;
            _revs = 0;
            return;
        }

        double revsPerSec = 0.35 + Math.Clamp(rpm, 0, 2000) / 2000.0 * 2.4;

        // RPM jitters every tick; only restart the spin for a change you could see.
        if (_revs > 0 && Math.Abs(revsPerSec - _revs) / _revs < 0.05) return;
        _revs = revsPerSec;
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
