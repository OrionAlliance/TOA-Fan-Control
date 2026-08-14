using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FanControlApp.Controls;

namespace GaugePreview;

/// <summary>
/// Renders the real gauge cluster offscreen to a PNG so its layout can actually
/// be looked at, instead of nudged blind and shipped to the user to check.
///
/// Note the needle renders at its rest position: UpdateMoving animates it with a
/// 350ms DoubleAnimation and RenderTargetBitmap captures immediately, so the
/// needle never gets there. Fine - this exists to check the static furniture
/// (labels, value text, ticks), not the needle.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        _ = new Application();

        // Same geometry as the real card: UniformGrid, 4 columns, 200 tall.
        const double w = 880;
        const double h = 200;

        var host = new Grid
        {
            Width = w,
            Height = h,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop((Color)ColorConverter.ConvertFromString("#232834")!, 0),
                    new GradientStop((Color)ColorConverter.ConvertFromString("#1A1D26")!, 0.5),
                    new GradientStop((Color)ColorConverter.ConvertFromString("#15181F")!, 1),
                },
            },
        };

        var cluster = new UniformGrid { Columns = 4 };
        host.Children.Add(cluster);

        cluster.Children.Add(Make("CPU", "C", 0, 100, 20, 36, greenTo: 70, redFrom: 90,
                                  peak: 40, loadValue: 13, peakLoad: 15));
        cluster.Children.Add(Make("GPU", "C", 0, 100, 20, 40, greenTo: 70, redFrom: 90,
                                  peak: 40, loadValue: 11, peakLoad: 14));
        cluster.Children.Add(Make("Chassis\nFan #2", "", 0, 2000, 1000, 418));
        cluster.Children.Add(Make("Chassis\nFan #3", "", 0, 2000, 1000, 559));

        // Animations only tick inside a real window's composition loop - a bare
        // offscreen visual captures everything at its rest position. Park a
        // borderless window far off-screen, let the 350ms sweeps finish, capture.
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -4000,
            Top = -4000,
            Width = w,
            Height = h,
            Content = host,
        };
        win.Show();

        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);

        var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        win.Close();

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));

        string outPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "cluster.png");

        using (var fs = File.Create(outPath)) enc.Save(fs);
        Console.WriteLine($"wrote {outPath}");
    }

    private static Gauge Make(string label, string unit, double min, double max,
                              double major, double value,
                              double greenTo = double.NaN, double redFrom = double.NaN,
                              double peak = double.NaN, double loadValue = double.NaN,
                              double peakLoad = double.NaN)
    {
        var g = new Gauge
        {
            Label = label,
            Unit = unit,
            Minimum = min,
            Maximum = max,
            MajorTick = major,
            GreenTo = greenTo,
            RedFrom = redFrom,
        };

        // After the properties, so the rebuild it triggers sees them all.
        g.Value = value;
        g.Peak = peak;
        g.LoadValue = loadValue;
        g.PeakLoad = peakLoad;
        return g;
    }
}
