// Draws the static dial face: bezel, color bands, ticks, gloss and text.
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
    /// <summary>Machined ring, dished face, and the shadow the rim casts inward.</summary>
    private void DrawBezel()
    {
        // Lit from the top-left.
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

        // Dished face, brightest at the upper-left.
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

        // Inner shadow so the face sits below the rim.
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
            green.ToolTip = Tip("Safe and full speed - the chip boosts unhindered here.");
            Face.Children.Add(green);
        }

        // Amber tax zone: safe, but boost erodes as heat climbs.
        if (!double.IsNaN(GreenTo) && !double.IsNaN(RedFrom) && RedFrom > GreenTo)
        {
            Path amber = Arc(AngleFor(GreenTo), AngleFor(RedFrom), BandR, B("#E8D44C"), 7);
            amber.Effect = new DropShadowEffect
            {
                Color = C("#E8D44C"), BlurRadius = 9, ShadowDepth = 0, Opacity = 0.45,
            };
            amber.ToolTip = Tip("Safe, but the tax zone - no damage, yet every degree here quietly costs a little boost speed.");
            Face.Children.Add(amber);
        }

        if (!double.IsNaN(RedFrom) && RedFrom < Maximum)
        {
            Path red = Arc(AngleFor(RedFrom), EndAngle, BandR, B("#F85149"), 7);
            red.Effect = new DropShadowEffect
            {
                Color = C("#F85149"), BlurRadius = 9, ShadowDepth = 0, Opacity = 0.55,
            };
            red.ToolTip = Tip("Hard throttle - the chip slams its own brakes to protect itself. Don't live here.");
            Face.Children.Add(red);
        }

        // Redline mark.
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

        // Numbered values get long heavy ticks; unnumbered midpoints get short faint ones.
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

            string text = v.ToString("0", CultureInfo.InvariantCulture);

            var tb = new TextBlock { Text = text, Foreground = num, FontSize = 9 };
            tb.Measure(new Size(100, 100));
            Point np = PointAt(a, NumberR);
            Canvas.SetLeft(tb, np.X - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, np.Y - tb.DesiredSize.Height / 2);
            Face.Children.Add(tb);
        }
    }

    /// <summary>Glass highlight across the upper face, clipped to the dial.</summary>
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
            // Clip in the gloss's own coordinates so it can't spill past the rim.
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
            IsHitTestVisible = false, // its layout box must not eat band tooltips
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
            IsHitTestVisible = false, // its box overlaps the red band
            LineHeight = 12,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        Canvas.SetLeft(lab, _cx - _r * 0.8);

        // Anchor the label's bottom at 0.86r, below the band ends.
        lab.Measure(new Size(_r * 1.6, double.PositiveInfinity));
        Canvas.SetTop(lab, _cy + _r * 0.86 - lab.DesiredSize.Height);
        Face.Children.Add(lab);
    }
}
