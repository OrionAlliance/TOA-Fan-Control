using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlyphSheet;

/// <summary>
/// Renders a labelled contact sheet of Segoe MDL2 Assets so a glyph can be PICKED
/// by looking at it, rather than guessed from a half-remembered hex code.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        int start = args.Length > 0 ? Convert.ToInt32(args[0], 16) : 0xE700;
        int end = args.Length > 1 ? Convert.ToInt32(args[1], 16) : 0xE7FF;
        string outPath = args.Length > 2
            ? args[2]
            : Path.Combine(AppContext.BaseDirectory, "glyphs.png");

        const int cols = 16;
        const int cellW = 62;
        const int cellH = 58;

        int count = end - start + 1;
        int rows = (count + cols - 1) / cols;
        int w = cols * cellW;
        int h = rows * cellH;

        var font = new FontFamily("Segoe MDL2 Assets");
        var typeface = new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var labelFace = new Typeface("Consolas");

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x26)), null,
                new Rect(0, 0, w, h));

            for (int i = 0; i < count; i++)
            {
                int code = start + i;
                double x = i % cols * cellW;
                double y = i / cols * cellH;

                var glyph = new FormattedText(
                    char.ConvertFromUtf32(code), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 26,
                    Brushes.White, 96);

                dc.DrawText(glyph, new Point(x + (cellW - glyph.Width) / 2, y + 4));

                var label = new FormattedText(
                    code.ToString("X4"), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, labelFace, 9,
                    new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA6)), 96);

                dc.DrawText(label, new Point(x + (cellW - label.Width) / 2, y + 40));
            }
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outPath)) enc.Save(fs);

        Console.WriteLine($"wrote {outPath}  ({count} glyphs, U+{start:X4}-U+{end:X4})");
    }
}
