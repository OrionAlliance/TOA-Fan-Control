using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGen;

/// <summary>
/// Draws the TOA - Fan Control icon: the app's own gauge, reduced to what still
/// reads when it's tiny.
///
/// Same palette and same light source (upper-left) as the live gauges, but this
/// deliberately does NOT reuse the Gauge control - that draws ticks, scale
/// numbers and a value readout, all of which are grey mush below ~48px. An icon
/// needs fewer marks than the thing it depicts.
///
/// What survives at 16px, and therefore what the icon is: a dark disc, one bright
/// arc, one strong diagonal needle. Detail is added back only at sizes with room
/// for it.
/// </summary>
internal static class Program
{
    // Same sweep as the real dial: 135deg (bottom-left) round the top to 405deg.
    private const double StartAngle = 135;
    private const double SweepAngle = 270;
    private const double EndAngle = StartAngle + SweepAngle;

    // Sitting in the middle of the amber: working, not redlining. At 330 it lands
    // on the amber/red boundary and reads as an alarm.
    private const double NeedleAngle = 313;

    private static readonly int[] Sizes = { 256, 128, 64, 48, 32, 16 };

    [STAThread]
    private static void Main(string[] args)
    {
        string outPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "Icon.ico");

        var frames = new List<byte[]>();
        var bitmaps = new List<BitmapSource>();
        foreach (int size in Sizes)
        {
            BitmapSource bmp = Render(size);
            bitmaps.Add(bmp);
            frames.Add(EncodePng(bmp));
            Console.WriteLine($"rendered {size}x{size}");
        }

        File.WriteAllBytes(outPath, PackIco(Sizes, frames));
        Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length} bytes, {Sizes.Length} sizes)");

        string sheet = Path.Combine(AppContext.BaseDirectory, "sheet.png");
        File.WriteAllBytes(sheet, EncodePng(ContactSheet(bitmaps)));
        Console.WriteLine($"wrote {sheet}");
    }

    /// <summary>
    /// A proof sheet: every size at life size along the top (what you'll actually
    /// see), and the small ones blown up nearest-neighbour underneath so the
    /// pixels can be judged honestly. Small-size legibility is the whole bet.
    /// </summary>
    private static BitmapSource ContactSheet(List<BitmapSource> bitmaps)
    {
        const int zoom = 8;
        const int pad = 16;

        int topH = 256;
        int bottomH = 48 * zoom;
        int w = pad + 256 + pad + 128 + pad + 64 + pad + 48 + pad + 32 + pad + 16 + pad;
        int zoomW = pad + 48 * zoom + pad + 32 * zoom + pad + 16 * zoom + pad;
        w = Math.Max(w, zoomW);
        int h = pad + topH + pad + bottomH + pad;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);

        using (DrawingContext dc = visual.RenderOpen())
        {
            // Mid grey - shows both the dark dial and any light fringing.
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x6E, 0x74, 0x80)), null,
                new Rect(0, 0, w, h));

            double x = pad;
            for (int i = 0; i < bitmaps.Count; i++)
            {
                int size = Sizes[i];
                dc.DrawImage(bitmaps[i], new Rect(x, pad + (topH - size) / 2.0, size, size));
                x += size + pad;
            }

            // 48 / 32 / 16 magnified - the sizes that decide whether this works.
            x = pad;
            double y = pad + topH + pad;
            foreach (int size in new[] { 48, 32, 16 })
            {
                int idx = Array.IndexOf(Sizes, size);
                dc.DrawImage(bitmaps[idx], new Rect(x, y, size * zoom, size * zoom));
                x += size * zoom + pad;
            }
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            Draw(dc, size);

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    // ---- the drawing --------------------------------------------------------

    private static void Draw(DrawingContext dc, int size)
    {
        double s = size;
        var c = new Point(s / 2, s / 2);

        // Small icons get no rim inset - every pixel counts.
        double outerInset = size >= 64 ? s * 0.02 : 0;
        double rBezel = s / 2 - outerInset;
        double rFace = rBezel * (size >= 32 ? 0.88 : 0.90);

        bool showBlades = size >= 128;   // the secret, only where it's legible
        bool showGloss = size >= 64;
        bool showHub = size >= 24;

        DrawBezel(dc, c, rBezel, rFace, size);
        if (showBlades) DrawGhostBlades(dc, c, rFace);
        DrawArc(dc, c, rFace, size);
        if (showGloss) DrawGloss(dc, c, rFace);
        DrawNeedle(dc, c, rFace, size);
        if (showHub) DrawHub(dc, c, rFace, size);
    }

    private static void DrawBezel(DrawingContext dc, Point c, double rBezel, double rFace, int size)
    {
        // Machined ring, lit from the upper-left.
        dc.DrawEllipse(BezelBrush(), null, c, rBezel, rBezel);

        // Dished face: light pools upper-left, falls away to the rim.
        var face = new RadialGradientBrush
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
        };
        dc.DrawEllipse(face, null, c, rFace, rFace);

        if (size < 32) return;

        // Inner shadow - the face sits below the rim.
        var inner = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0.72),
                new GradientStop(Color.FromArgb(90, 0, 0, 0), 0.93),
                new GradientStop(Color.FromArgb(150, 0, 0, 0), 1),
            },
        };
        dc.DrawEllipse(inner, null, c, rFace, rFace);
    }

    private static Brush BezelBrush() => new LinearGradientBrush
    {
        StartPoint = new Point(0.15, 0),
        EndPoint = new Point(0.85, 1),
        GradientStops =
        {
            new GradientStop(C("#5A6478"), 0),
            new GradientStop(C("#232735"), 0.45),
            new GradientStop(C("#171A23"), 0.72),
            new GradientStop(C("#3A4150"), 1),
        },
    };

    /// <summary>
    /// Faint impeller blades ghosted into the dial face - visible up close, gone
    /// by 64px. Strokes rather than filled wedges: at low alpha they suggest
    /// rotation without turning into a grey smear.
    /// </summary>
    private static void DrawGhostBlades(DrawingContext dc, Point c, double rFace)
    {
        dc.PushClip(new EllipseGeometry(c, rFace, rFace));

        double rIn = rFace * 0.20;
        double rOut = rFace * 0.80;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), rFace * 0.10)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        for (int i = 0; i < 6; i++)
        {
            double a = i * 60;
            Point p0 = At(c, a, rIn);
            Point p1 = At(c, a + 16, (rIn + rOut) / 2 + rOut * 0.16);
            Point p2 = At(c, a + 52, rOut);

            var fig = new PathFigure { StartPoint = p0 };
            fig.Segments.Add(new QuadraticBezierSegment(p1, p2, true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);

            dc.DrawGeometry(null, pen, geo);
        }

        dc.Pop();
    }

    /// <summary>The band: green, into amber, into red. This is the icon's loudest signal.</summary>
    private static void DrawArc(DrawingContext dc, Point c, double rFace, int size)
    {
        double r = rFace * 0.82;
        double thickness = rFace * (size <= 16 ? 0.26 : size <= 32 ? 0.22 : 0.18);

        double greenEnd = StartAngle + SweepAngle * 0.44;
        double amberEnd = StartAngle + SweepAngle * 0.75;

        // Recessed track, skipped when small - it only muddies the colour.
        if (size >= 48)
            Stroke(dc, c, StartAngle, EndAngle, r, C("#323848"), thickness, 1);

        Stroke(dc, c, StartAngle, greenEnd, r, C("#3FB950"), thickness, 1);
        Stroke(dc, c, greenEnd, amberEnd, r, C("#D29922"), thickness, 1);
        Stroke(dc, c, amberEnd, EndAngle, r, C("#F85149"), thickness, 1);
    }

    private static void DrawGloss(DrawingContext dc, Point c, double rFace)
    {
        dc.PushClip(new EllipseGeometry(c, rFace, rFace));

        var gloss = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(34, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(12, 255, 255, 255), 0.55),
                new GradientStop(Colors.Transparent, 1),
            },
        };

        dc.DrawEllipse(gloss, null,
            new Point(c.X, c.Y - rFace * 0.45), rFace * 0.88, rFace * 0.58);

        dc.Pop();
    }

    private static void DrawNeedle(DrawingContext dc, Point c, double rFace, int size)
    {
        double len = rFace * 0.90;
        double tail = rFace * 0.18;

        // Gets proportionally fatter as the icon shrinks. A needle scaled linearly
        // is ~1px at 16 and antialiases into nothing - the exact failure this icon
        // is built to avoid.
        double halfWidth = rFace * size switch
        {
            <= 16 => 0.22,
            <= 32 => 0.15,
            <= 48 => 0.11,
            _ => 0.085,
        };

        // Built along +X then rotated, same as the live gauge's needle.
        var fig = new PathFigure { StartPoint = new Point(len, 0), IsClosed = true };
        fig.Segments.Add(new LineSegment(new Point(0, -halfWidth), true));
        fig.Segments.Add(new LineSegment(new Point(-tail, 0), true));
        fig.Segments.Add(new LineSegment(new Point(0, halfWidth), true));

        var geo = new PathGeometry();
        geo.Figures.Add(fig);

        var t = new TransformGroup();
        t.Children.Add(new RotateTransform(NeedleAngle));
        t.Children.Add(new TranslateTransform(c.X, c.Y));
        geo.Transform = t;

        // Big enough to shade: gradient across its width reads as a rounded edge.
        // Small: flat white. The gradient's dark side is half the needle at 32px,
        // and that half simply disappears against the face.
        Brush brush = size >= 48
            ? new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(C("#FFFFFF"), 0),
                    new GradientStop(C("#F2F5FA"), 0.5),
                    new GradientStop(C("#B6BECD"), 1),
                },
            }
            : Brushes.White;

        // Keyline only where the needle is wide enough to still have a middle
        // after it's outlined.
        Pen? edge = size >= 64
            ? new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), rFace * 0.018)
            : null;

        dc.DrawGeometry(brush, edge, geo);
    }

    private static void DrawHub(DrawingContext dc, Point c, double rFace, int size)
    {
        double r = rFace * (size <= 48 ? 0.13 : 0.11);

        // Bright enough to actually be a dome. The old one bottomed out darker
        // than the face, so it read as a hole rather than a hub.
        var hub = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.3),
            GradientStops =
            {
                new GradientStop(C("#9AA4B8"), 0),
                new GradientStop(C("#525C72"), 0.6),
                new GradientStop(C("#2B3242"), 1),
            },
        };

        dc.DrawEllipse(hub, null, c, r, r);
    }

    // ---- helpers ------------------------------------------------------------

    private static void Stroke(DrawingContext dc, Point c, double a0, double a1,
                               double r, Color color, double thickness, double opacity)
    {
        var fig = new PathFigure { StartPoint = At(c, a0, r) };
        fig.Segments.Add(new ArcSegment
        {
            Point = At(c, a1, r),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = a1 - a0 > 180,
        });

        var geo = new PathGeometry();
        geo.Figures.Add(fig);

        var brush = new SolidColorBrush(color) { Opacity = opacity };
        dc.DrawGeometry(null, new Pen(brush, thickness), geo);
    }

    private static Point At(Point c, double angleDeg, double radius)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new Point(c.X + radius * Math.Cos(rad), c.Y + radius * Math.Sin(rad));
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Standard ICO container with PNG-compressed frames (fine on Vista+, and this
    /// is a Windows 11 app). 256px is written as 0 in the width/height bytes -
    /// that's the format's way of saying 256.
    /// </summary>
    private static byte[] PackIco(int[] sizes, List<byte[]> frames)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        w.Write((short)0);              // reserved
        w.Write((short)1);              // type: icon
        w.Write((short)frames.Count);

        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);           // palette count
            w.Write((byte)0);           // reserved
            w.Write((short)1);          // colour planes
            w.Write((short)32);         // bits per pixel
            w.Write(frames[i].Length);
            w.Write(offset);
            offset += frames[i].Length;
        }

        foreach (byte[] f in frames) w.Write(f);

        w.Flush();
        return ms.ToArray();
    }
}
