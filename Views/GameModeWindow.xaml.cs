using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

/// <summary>Display-only always-on-top readout; Topmost can't draw over exclusive fullscreen, only borderless.</summary>
public partial class GameModeWindow : Window
{
    private readonly FanController _controller;

    private bool _forceClose;

    /// <summary>Raised when the user wants the full window back.</summary>
    public event EventHandler? RestoreRequested;

    public GameModeWindow(FanController controller)
    {
        InitializeComponent();
        _controller = controller;

        RestorePlacement();

        _controller.Updated += OnUpdated;

        // Alt+F4 restores the app instead of leaving it running with every window hidden.
        Closing += (_, e) =>
        {
            if (_forceClose) return;
            e.Cancel = true;
            RestoreRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Really closes on app shutdown, bypassing the Closing cancel above.</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Detach();
        Close();
    }

    /// <summary>The saved spot if it's still on a screen (monitors can change), else top-centre, clear of most HUDs.</summary>
    private void RestorePlacement()
    {
        FanSettings s = _controller.Settings;
        double x = SystemParameters.VirtualScreenLeft, y = SystemParameters.VirtualScreenTop;
        double w = SystemParameters.VirtualScreenWidth, h = SystemParameters.VirtualScreenHeight;

        bool onScreen = !double.IsNaN(s.OverlayLeft) && !double.IsNaN(s.OverlayTop)
                        && s.OverlayLeft >= x && s.OverlayTop >= y
                        && s.OverlayLeft <= x + w - 60 && s.OverlayTop <= y + h - 40;

        Left = onScreen ? s.OverlayLeft : (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = onScreen ? s.OverlayTop : 24;
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
        // Stays subscribed while hidden for reuse, so skip painting when not on screen.
        if (!IsVisible) return;

        CpuText.Text = r.CpuTemp is { } c ? $"{c:F0}" : "--";
        CpuText.Foreground = TempBrush(r.CpuTemp);
        GpuText.Text = r.GpuTemp is { } g ? $"{g:F0}" : "--";
        GpuText.Foreground = TempBrush(r.GpuTemp);

        FanText.Text = $"{r.OutputPercent:F0}%";
        // Red when whatever's driving the fans is genuinely hot.
        FanText.Foreground = r.SourceTemp is >= 85 ? Res("Hot") : Res("Text");

        // The controller's session peaks, shared with the dials and bars so no view disagrees.
        CpuPeakText.Text = float.IsNaN(r.PeakCpu) ? "peak --" : $"peak {r.PeakCpu:F0}";
        GpuPeakText.Text = float.IsNaN(r.PeakGpu) ? "peak --" : $"peak {r.PeakGpu:F0}";

        // RPM of every fan the app drives, not a hardcoded list.
        var rpms = r.DrivenFans
            .Select(name => r.Fans.FirstOrDefault(f =>
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f?.Rpm is { } rpm and >= 1 ? $"{rpm:F0}" : "--")
            .ToList();
        RpmText.Text = rpms.Count > 0 ? string.Join(" / ", rpms) : "--";
    }

    /// <summary>Drag to place it; double-click anywhere to come back.</summary>
    private void OnDragOrRestore(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            RestoreRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        Infrastructure.WindowDrag.TryDrag(this);

        SavePlacement();
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e) =>
        RestoreRequested?.Invoke(this, EventArgs.Empty);

    private void Detach() => _controller.Updated -= OnUpdated;

    private Brush Res(string key) => (Brush)FindResource(key);

    private Brush TempBrush(float? v) => v switch
    {
        null => Res("TextDim"),
        < 60 => Res("Cool"),
        < 78 => Res("Warm"),
        _ => Res("Hot"),
    };
}
