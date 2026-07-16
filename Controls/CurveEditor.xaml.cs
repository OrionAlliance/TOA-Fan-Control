using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FanControlApp.Helpers;

namespace FanControlApp.Controls;

/// <summary>
/// The fan curve as a draggable line: temperature across the bottom, fan percent
/// up the side. Drag a dot to move it, double-click empty space to add one,
/// right-click a dot to delete it.
/// </summary>
public partial class CurveEditor : UserControl
{
    private const float MinTemp = 20f;
    private const float MaxTemp = 100f;
    private const double HitRadius = 14;

    private const double PadLeft = 36;
    private const double PadRight = 12;
    private const double PadTop = 12;
    private const double PadBottom = 24;

    private FanCurve _curve = FanCurve.Default();
    private CurvePoint? _dragging;
    private float? _currentTemp;
    private float _currentPercent;

    public event EventHandler? CurveChanged;

    public CurveEditor()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
        MouseLeftButtonDown += OnLeftDown;
        MouseLeftButtonUp += OnLeftUp;
        MouseMove += OnMove;
        MouseRightButtonDown += OnRightDown;
        MouseLeave += (_, _) => EndDrag();
    }

    public FanCurve Curve
    {
        get => _curve;
        set
        {
            _curve = value;
            Redraw();
        }
    }

    /// <summary>Live readout marker - where the machine actually is right now.</summary>
    public void SetLive(float? temp, float percent)
    {
        _currentTemp = temp;
        _currentPercent = percent;
        Redraw();
    }

    // ---- coordinate mapping -------------------------------------------------

    private double PlotW => Math.Max(1, ActualWidth - PadLeft - PadRight);
    private double PlotH => Math.Max(1, ActualHeight - PadTop - PadBottom);

    private double TempToX(float t) =>
        PadLeft + (Math.Clamp(t, MinTemp, MaxTemp) - MinTemp) / (MaxTemp - MinTemp) * PlotW;

    private double PctToY(float p) =>
        PadTop + (1 - Math.Clamp(p, 0, 100) / 100.0) * PlotH;

    private float XToTemp(double x) =>
        (float)Math.Clamp(MinTemp + (x - PadLeft) / PlotW * (MaxTemp - MinTemp), MinTemp, MaxTemp);

    private float YToPct(double y) =>
        (float)Math.Clamp((1 - (y - PadTop) / PlotH) * 100.0, 0, 100);

    // ---- interaction --------------------------------------------------------

    private CurvePoint? HitTest(Point p)
    {
        CurvePoint? best = null;
        double bestDist = HitRadius;

        foreach (CurvePoint cp in _curve.Points)
        {
            double dx = TempToX(cp.Temp) - p.X;
            double dy = PctToY(cp.Percent) - p.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d > bestDist) continue;
            bestDist = d;
            best = cp;
        }

        return best;
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        Point p = e.GetPosition(this);
        CurvePoint? hit = HitTest(p);

        if (e.ClickCount == 2 && hit == null)
        {
            var added = new CurvePoint(XToTemp(p.X), YToPct(p.Y));
            _curve.Points.Add(added);
            _dragging = added;
            CaptureMouse();
            Redraw();
            CurveChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (hit == null) return;

        _dragging = hit;
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragging == null) return;

        Point p = e.GetPosition(this);
        _dragging.Temp = XToTemp(p.X);
        _dragging.Percent = YToPct(p.Y);
        Redraw();
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_dragging == null) return;
        _dragging = null;
        ReleaseMouseCapture();
        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        CurvePoint? hit = HitTest(e.GetPosition(this));
        if (hit == null) return;

        // A curve needs at least two points to mean anything.
        if (_curve.Points.Count <= 2) return;

        _curve.Points.Remove(hit);
        Redraw();
        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- rendering ----------------------------------------------------------

    private static Brush B(string hex) =>
        (Brush)new BrushConverter().ConvertFromString(hex)!;

    private void Redraw()
    {
        Surface.Children.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        DrawGrid();
        DrawLiveMarker();
        DrawCurve();
    }

    private void DrawGrid()
    {
        Brush grid = B("#252A36");
        Brush label = B("#8A92A6");

        for (int t = 20; t <= 100; t += 10)
        {
            double x = TempToX(t);
            Surface.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = PadTop, Y2 = PadTop + PlotH,
                Stroke = grid, StrokeThickness = 1,
            });

            var tb = new TextBlock
            {
                Text = t.ToString(CultureInfo.InvariantCulture),
                Foreground = label,
                FontSize = 10,
            };
            Canvas.SetLeft(tb, x - 8);
            Canvas.SetTop(tb, PadTop + PlotH + 5);
            Surface.Children.Add(tb);
        }

        for (int p = 0; p <= 100; p += 25)
        {
            double y = PctToY(p);
            Surface.Children.Add(new Line
            {
                X1 = PadLeft, X2 = PadLeft + PlotW, Y1 = y, Y2 = y,
                Stroke = grid, StrokeThickness = 1,
            });

            var tb = new TextBlock
            {
                Text = p + "%",
                Foreground = label,
                FontSize = 10,
            };
            Canvas.SetLeft(tb, 4);
            Canvas.SetTop(tb, y - 8);
            Surface.Children.Add(tb);
        }
    }

    private void DrawLiveMarker()
    {
        if (_currentTemp is not { } temp) return;

        double x = TempToX(temp);
        Surface.Children.Add(new Line
        {
            X1 = x, X2 = x, Y1 = PadTop, Y2 = PadTop + PlotH,
            Stroke = B("#4C8DFF"), StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            Opacity = 0.85,
        });

        var dot = new Ellipse
        {
            Width = 9, Height = 9,
            Fill = B("#4C8DFF"),
        };
        Canvas.SetLeft(dot, x - 4.5);
        Canvas.SetTop(dot, PctToY(_currentPercent) - 4.5);
        Surface.Children.Add(dot);
    }

    private void DrawCurve()
    {
        List<CurvePoint> pts = _curve.Sorted.ToList();
        if (pts.Count == 0) return;

        var poly = new Polyline
        {
            Stroke = B("#E6E9F0"),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };

        // Flat run in from the left edge, and out to the right - that's what the
        // curve actually does outside its end points.
        poly.Points.Add(new Point(PadLeft, PctToY(pts[0].Percent)));
        foreach (CurvePoint cp in pts)
            poly.Points.Add(new Point(TempToX(cp.Temp), PctToY(cp.Percent)));
        poly.Points.Add(new Point(PadLeft + PlotW, PctToY(pts[^1].Percent)));

        Surface.Children.Add(poly);

        foreach (CurvePoint cp in pts)
        {
            var e = new Ellipse
            {
                Width = 11, Height = 11,
                Fill = B("#12141A"),
                Stroke = ReferenceEquals(cp, _dragging) ? B("#4C8DFF") : B("#E6E9F0"),
                StrokeThickness = 2,
                Cursor = Cursors.Hand,
            };
            Canvas.SetLeft(e, TempToX(cp.Temp) - 5.5);
            Canvas.SetTop(e, PctToY(cp.Percent) - 5.5);
            Surface.Children.Add(e);
        }
    }
}
