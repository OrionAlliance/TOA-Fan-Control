// Draws the fan frame, blades, hub and label.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FanControlApp.Controls;

public partial class FanBlade
{
    /// <summary>The square housing: rounded metal frame, four screws, a dished bore.</summary>
    private void DrawFrame()
    {
        double side = _r * 2 + 10;
        double left = _cx - side / 2;
        double top = _cy - side / 2;

        // Brushed metal frame, lit from the top-left like the dials.
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

        // Corner screws.
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

        // Round recess the blades sit inside.
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

    /// <summary>Blades around the hub; the whole layer spins as one.</summary>
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

        _spin = new RotateTransform(0, _cx, _cy);
        _revs = 0; // fresh transform, nothing spinning yet
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

        // Bowed edges give an airfoil curve instead of a flat paddle.
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

    /// <summary>Fan name under the frame; it sits on the card, so it follows the theme text colour.</summary>
    private void DrawLabel()
    {
        var lab = new TextBlock
        {
            Text = Label,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            Width = ActualWidth,
        };
        lab.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        Canvas.SetLeft(lab, 0);
        Canvas.SetTop(lab, ActualHeight - LabelBand + 4);
        FrameLayer.Children.Add(lab);
    }
}
