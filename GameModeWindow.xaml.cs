using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FanControlApp.Helpers;

namespace FanControlApp;

/// <summary>
/// The tiny always-on-top readout for while you're playing. Display only - it
/// shows what the controller reports and never touches a fan.
///
/// Caveat worth knowing: Topmost cannot draw over a game in EXCLUSIVE fullscreen.
/// Windows hands the display to the game and nothing else gets a look in. Works
/// fine over Windowed Fullscreen / Borderless.
/// </summary>
public partial class GameModeWindow : Window
{
    private const string Fan2 = "Chassis Fan #2";
    private const string Fan3 = "Chassis Fan #3";

    private readonly FanController _controller;

    private double _peakCpu = double.NaN;
    private double _peakGpu = double.NaN;
    private bool _forceClose;

    /// <summary>Raised when the user wants the full window back.</summary>
    public event EventHandler? RestoreRequested;

    public GameModeWindow(FanController controller)
    {
        InitializeComponent();
        _controller = controller;

        RestorePlacement();

        _controller.Updated += OnUpdated;

        // Alt+F4 on the overlay should bring the app back, not leave it running
        // with every window hidden and no way to reach it.
        Closing += (_, e) =>
        {
            if (_forceClose) return;
            e.Cancel = true;
            RestoreRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>
    /// Actually close, for app shutdown. Without this the cancel above would
    /// refuse the close and leave the process alive with no windows.
    /// </summary>
    public void ForceClose()
    {
        _forceClose = true;
        Detach();
        Close();
    }

    private void RestorePlacement()
    {
        FanSettings s = _controller.Settings;

        if (!double.IsNaN(s.OverlayLeft) && !double.IsNaN(s.OverlayTop))
        {
            Left = s.OverlayLeft;
            Top = s.OverlayTop;
            EnsureOnScreen();
            return;
        }

        // Never placed: top-centre, out of the way of most HUDs.
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 24;
    }

    /// <summary>A saved position can be off-screen now if the monitor setup changed.</summary>
    private void EnsureOnScreen()
    {
        double w = SystemParameters.VirtualScreenWidth;
        double h = SystemParameters.VirtualScreenHeight;
        double x = SystemParameters.VirtualScreenLeft;
        double y = SystemParameters.VirtualScreenTop;

        if (Left < x || Top < y || Left > x + w - 60 || Top > y + h - 40)
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 24;
        }
    }

    public void SavePlacement() =>
        _controller.UpdateSettings(s =>
        {
            s.OverlayLeft = Left;
            s.OverlayTop = Top;
        });

    private void OnUpdated(object? sender, FanReadings r) =>
        Dispatcher.BeginInvoke(() => Render(r));

    private void Render(FanReadings r)
    {
        CpuText.Text = r.CpuTemp is { } c ? $"{c:F0}" : "--";
        CpuText.Foreground = TempBrush(r.CpuTemp);
        GpuText.Text = r.GpuTemp is { } g ? $"{g:F0}" : "--";
        GpuText.Foreground = TempBrush(r.GpuTemp);

        FanText.Text = $"{r.OutputPercent:F0}%";
        // Red when whatever's driving the fans is genuinely hot.
        FanText.Foreground = r.SourceTemp is >= 85 ? Res("Hot") : Res("Text");

        // Its own peaks, deliberately: this window is for watching one gaming
        // session, so they start fresh each time you drop into it.
        if (r.CpuTemp is { } cv && (double.IsNaN(_peakCpu) || cv > _peakCpu)) _peakCpu = cv;
        if (r.GpuTemp is { } gv && (double.IsNaN(_peakGpu) || gv > _peakGpu)) _peakGpu = gv;

        CpuPeakText.Text = double.IsNaN(_peakCpu) ? "peak --" : $"peak {_peakCpu:F0}";
        GpuPeakText.Text = double.IsNaN(_peakGpu) ? "peak --" : $"peak {_peakGpu:F0}";

        RpmText.Text = $"{RpmOf(r, Fan2)} / {RpmOf(r, Fan3)}";
    }

    private static string RpmOf(FanReadings r, string name)
    {
        FanChannel? f = r.Fans.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return f?.Rpm is { } rpm and >= 1 ? $"{rpm:F0}" : "--";
    }

    public void ResetPeaks()
    {
        _peakCpu = double.NaN;
        _peakGpu = double.NaN;
    }

    /// <summary>Drag to place it; double-click anywhere to come back.</summary>
    private void OnDragOrRestore(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            RestoreRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws if the button was already released. Nothing to do.
        }

        SavePlacement();
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e) =>
        RestoreRequested?.Invoke(this, EventArgs.Empty);

    public void Detach() => _controller.Updated -= OnUpdated;

    private Brush Res(string key) => (Brush)FindResource(key);

    private Brush TempBrush(float? v) => v switch
    {
        null => Res("TextDim"),
        < 60 => Res("Cool"),
        < 78 => Res("Warm"),
        _ => Res("Hot"),
    };
}
