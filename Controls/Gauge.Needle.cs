// Builds the needle, hub cap, and the peak and load marks.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FanControlApp.Controls;

public partial class Gauge
{
    // ---- needle + peak ------------------------------------------------------

    private void BuildMoving()
    {
        double len = NeedleLen;

        // Yellow peak mark, drawn under the needle so the needle wins on overlap.
        _peakRotate = new RotateTransform(StartAngle);
        (_peakHit, _peakMark) = AddPeakMark(_peakRotate, 18, "#E3B341", "#E3B341", 0.85);

        // Peak-load mark in electric cyan, since the triangle's deep cyan vanishes over the green band.
        _loadPeakRotate = new RotateTransform(StartAngle);
        (_loadPeakHit, _loadPeakMark) = AddPeakMark(_loadPeakRotate, 14, "#4DEEFF", "#00A3C4", 0.9);

        // Live-load triangle outside the band, so a load never reads as a temperature.
        _loadRotate = new RotateTransform(StartAngle);
        var loadTransform = Pivot(_loadRotate);
        var loadPoints = new PointCollection
        {
            new Point(BandR + 4, 0),
            new Point(BandR + 12, -4.5),
            new Point(BandR + 12, 4.5),
        };
        _loadHit = new Polygon
        {
            Points = loadPoints,
            Fill = Brushes.Transparent,
            Stroke = Brushes.Transparent,
            StrokeThickness = 14,
            RenderTransform = loadTransform,
            Cursor = Cursors.Hand,
        };
        Moving.Children.Add(_loadHit);

        _loadMark = new Polygon
        {
            Points = loadPoints,
            Fill = B("#00A3C4"),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = C("#00A3C4"), BlurRadius = 6, ShadowDepth = 0, Opacity = 0.8,
            },
            RenderTransform = loadTransform,
        };
        Moving.Children.Add(_loadMark);

        // White needle; gradient gives a rounded edge, shadow lifts it off the face.
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
            RenderTransform = Pivot(_needleRotate),
        };
        Moving.Children.Add(_needle);

        // Hub cap over the needle pivot.
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

    // Spins about the dial's center.
    private TransformGroup Pivot(RotateTransform rotate) =>
        new() { Children = { rotate, new TranslateTransform(_cx, _cy) } };

    // A peak tick on the band plus a fat invisible copy that catches the mouse (Transparent hit-tests, null wouldn't).
    private (Path Hit, Path Mark) AddPeakMark(RotateTransform rotate, double hitWidth, string stroke, string glow, double glowOpacity)
    {
        var fig = new PathFigure { StartPoint = new Point(BandR - 11, 0) };
        fig.Segments.Add(new LineSegment(new Point(BandR + 6, 0), true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        var transform = Pivot(rotate); // shared so the mark and its hit area never drift apart

        var hit = new Path
        {
            Data = geo,
            Stroke = Brushes.Transparent,
            StrokeThickness = hitWidth,
            RenderTransform = transform,
            Cursor = Cursors.Hand,
        };
        Moving.Children.Add(hit);

        var mark = new Path
        {
            Data = geo,
            Stroke = B(stroke),
            StrokeThickness = 3,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = C(glow), BlurRadius = 7, ShadowDepth = 0, Opacity = glowOpacity },
            RenderTransform = transform,
        };
        Moving.Children.Add(mark);
        return (hit, mark);
    }
}
