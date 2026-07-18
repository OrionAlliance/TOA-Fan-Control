using System.Windows;
using System.Windows.Media;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

/// <summary>One row in the "other fans" strip. Display shape only.</summary>
public sealed class FanRow
{
    public required string Name { get; init; }
    public required string Reading { get; init; }
    public required Brush NameBrush { get; init; }
    public required Brush ValueBrush { get; init; }
}

/// <summary>
/// Display only - it renders what the controller reports and forwards the two
/// actions (pause, Game Mode) back to it. No fan logic lives here, and there's
/// nothing to configure: the app just matches the fans to the hottest part.
/// </summary>
public partial class MainWindow : Window
{
    private const string Fan2 = "Chassis Fan #2";
    private const string Fan3 = "Chassis Fan #3";

    private readonly FanController _controller = App.Controller;
    private GameModeWindow? _overlay;
    private System.Windows.Forms.NotifyIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();

        SetupTray();

        // The redline on the temp gauges is the real one: the 5800X throttles at
        // 90C. Nothing below that is damage.
        CpuGauge.RedFrom = 90;
        GpuGauge.RedFrom = 90;

        // We draw the title bar ourselves, so the maximise glyph is kept in step
        // by hand. The DWM call still colours the window's outer border.
        StateChanged += OnWindowStateChanged;
        SourceInitialized += (_, _) => TitleBarColor.Apply(
            this,
            caption: ResColor("Panel"),
            text: ResColor("Text"),
            border: ResColor("PanelEdge"));

        _controller.Updated += OnUpdated;
        Closing += (_, _) =>
        {
            _controller.Updated -= OnUpdated;

            // The overlay cancels its own Closing (so Alt+F4 there just restores),
            // which would keep the process alive forever with no window. Tear it
            // down explicitly when the real window goes.
            _overlay?.ForceClose();
            _overlay = null;

            // Or it lingers in the tray as a ghost until you hover it.
            _tray?.Dispose();
            _tray = null;

            _controller.Dispose();
        };
    }

    // ---- system tray --------------------------------------------------------

    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            // The exe's own embedded icon - our dial - so the tray matches the app.
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "TOA - Fan Control",
            Visible = true,
        };

        _tray.DoubleClick += (_, _) => ShowFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
    }

    private void ShowFromTray()
    {
        // If they're in Game Mode, bring back the full window rather than stacking
        // it behind the overlay.
        if (_overlay is { IsVisible: true })
        {
            LeaveGameMode();
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnUpdated(object? sender, FanReadings r) => Dispatcher.BeginInvoke(() => Render(r));

    private void Render(FanReadings r)
    {
        CpuGauge.Value = r.CpuTemp ?? double.NaN;
        GpuGauge.Value = r.GpuTemp ?? double.NaN;
        Fan2Gauge.Value = RpmOf(r, Fan2);
        Fan3Gauge.Value = RpmOf(r, Fan3);

        StatusText.Text = r.Status;
        StatusText.Foreground = r.NoControllableFans || r.SentinelLost ? Res("Hot") : Res("TextDim");

        // Live tray tooltip, so you can hover it while minimised and see the state
        // without reopening. NotifyIcon.Text caps at 63 chars - keep it short.
        if (_tray != null)
        {
            string hot = r.SourceTemp is { } t ? $"{t:F0}°C" : "--";
            _tray.Text = $"TOA Fan Control  ·  {hot}  ·  fans {r.OutputPercent:F0}%";
        }

        UpdateReleaseButton();
        RenderOtherFans(r);
    }

    private static double RpmOf(FanReadings r, string name)
    {
        FanChannel? f = r.Fans.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return f?.Rpm ?? double.NaN;
    }

    /// <summary>Everything the app is NOT driving - the two it does drive have gauges.</summary>
    private void RenderOtherFans(FanReadings r)
    {
        FanList.ItemsSource = r.Fans
            .Where(f => f.RpmSensor != null)
            .Where(f => !string.Equals(f.Name, Fan2, StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(f.Name, Fan3, StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                bool dead = f.Rpm is null or < 1;
                return new FanRow
                {
                    Name = f.Name,
                    Reading = dead ? "--" : $"{f.Rpm:F0} rpm",
                    NameBrush = Res("TextDim"),
                    ValueBrush = dead ? Res("TextDim") : Res("Text"),
                };
            })
            .ToList();
    }

    // ---- the two actions ----------------------------------------------------

    /// <summary>Pause/resume: hand the fans back to the BIOS, or take them again.</summary>
    private void OnReleaseClick(object sender, RoutedEventArgs e)
    {
        if (_controller.IsPaused) _controller.Resume();
        else _controller.Pause();

        UpdateReleaseButton();
    }

    private void UpdateReleaseButton() =>
        ReleaseButton.Content = _controller.IsPaused ? "Take fans back" : "Hand fans to BIOS";

    private void OnResetPeaksClick(object sender, RoutedEventArgs e)
    {
        CpuGauge.ResetPeak();
        GpuGauge.ResetPeak();
        Fan2Gauge.ResetPeak();
        Fan3Gauge.ResetPeak();
        _overlay?.ResetPeaks();
    }

    // ---- game mode ----------------------------------------------------------

    /// <summary>
    /// Shrink to a small always-on-top readout. The full window is hidden, not
    /// closed - closing it would take the controller down with it and stop the
    /// fans being driven at all.
    /// </summary>
    private void OnGameModeClick(object sender, RoutedEventArgs e)
    {
        if (_overlay == null)
        {
            _overlay = new GameModeWindow(_controller);
            _overlay.RestoreRequested += (_, _) => LeaveGameMode();
        }

        _overlay.Show();
        _overlay.Activate();
        Hide();
        DebugLog.Write("Game Mode on - main window hidden, overlay up.");
    }

    private void LeaveGameMode()
    {
        _overlay?.SavePlacement();
        _overlay?.Hide();
        Show();
        Activate();
        DebugLog.Write("Game Mode off.");
    }

    // ---- caption buttons (ours, since we draw the title bar) -----------------

    // Minimise goes to the tray, not the taskbar - this is a set-and-forget
    // background app. Hide() drops the taskbar button; the tray icon brings it back.
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => Hide();

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Swap the glyph so it says what the button will DO, not what state it's in.</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool max = WindowState == WindowState.Maximized;
        MaxButton.Content = max ? "" : "";   // Segoe MDL2: restore / maximise
        MaxButton.ToolTip = max ? "Restore" : "Maximise";
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private Color ResColor(string key) => ((SolidColorBrush)FindResource(key)).Color;
}
