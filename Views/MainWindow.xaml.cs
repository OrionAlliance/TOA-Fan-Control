using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FanControlApp.Controls;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

/// <summary>Display only: renders controller readings and forwards user actions; no fan logic here.</summary>
public partial class MainWindow : Window
{
    private const int FansPerRow = 4;

    // Fan tile footprint, smaller than the temp dials; text keeps its minimum readable size.
    private const double FanTileW = 120;
    private const double FanTileH = 124;

    private readonly FanController _controller = App.Controller;
    private GameModeWindow? _overlay;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _titleBarReady;
    private bool _conflictNotified;

    // Per-fan tile and bar, latched once seen spinning; the bar's live RPM shows it isn't frozen.
    private readonly Dictionary<string, FanBlade> _fanGauges = new();
    private readonly Dictionary<string, StatBar> _fanBars = new();
    private readonly List<string> _shownFans = new();

    public MainWindow()
    {
        InitializeComponent();

        // Build version from the assembly into the caption and taskbar title.
        string label = $"TOA - Fan Control  {AppVersion.Display}";
        TitleText.Text = label;
        Title = label;

        SetupTray();

        ApplyDisplayStyle(_controller.Settings.DisplayStyle);

        // Custom title bar: sync the maximise glyph by hand; DWM border colour follows the theme.
        StateChanged += OnWindowStateChanged;
        SourceInitialized += (_, _) => { _titleBarReady = true; ApplyTitleBarColors(); };
        ThemeManager.Changed += OnThemeChanged;

        _controller.Updated += OnUpdated;
        Closing += (_, _) =>
        {
            _controller.Updated -= OnUpdated;
            ThemeManager.Changed -= OnThemeChanged;

            // The overlay cancels its own Closing, so force it or the process lives on windowless.
            _overlay?.ForceClose();
            _overlay = null;

            // Otherwise a ghost icon lingers in the tray until hovered.
            _tray?.Dispose();
            _tray = null;

            _controller.Dispose();
        };
    }

    private void OnUpdated(object? sender, FanReadings r) => Dispatcher.BeginInvoke(() => Render(r));

    private void Render(FanReadings r)
    {
        // Tray tooltip first, it matters while hidden; NotifyIcon.Text caps at 63 chars.
        if (_tray != null)
        {
            string hot = r.SourceTemp is { } t ? $"{t:F0}°C" : "--";
            // While the BIOS owns the fans, never claim a % nobody's driving.
            string fans = r.BiosHasFans ? "BIOS" : $"{r.OutputPercent:F0}%";
            _tray.Text = $"TOA - Fan Control  ·  {hot}  ·  fans {fans}";
        }

        // One balloon per conflict episode; the real fix is in the other program.
        if (r.Conflict && !_conflictNotified && _tray != null)
        {
            _conflictNotified = true;
            _tray.ShowBalloonTip(8000, "TOA - Fan Control",
                "Another program is also changing your fan speeds - holding your speeds " +
                "steady. For a real fix, turn off fan control in that app (RGB suites " +
                "like SignalRGB often switch it on after updates).",
                System.Windows.Forms.ToolTipIcon.Warning);
        }
        else if (!r.Conflict)
        {
            _conflictNotified = false;
        }

        // Hidden or minimized: skip painting; peaks live in the controller, so nothing is lost.
        if (!IsVisible || WindowState == WindowState.Minimized) return;

        // Only the shown panel updates, since unseen dial animations still cost frames.
        if (DialsPanel.Visibility == Visibility.Visible)
        {
            CpuGauge.Value = r.CpuTemp ?? double.NaN;
            GpuGauge.Value = r.GpuTemp ?? double.NaN;
            GpuGauge.TrueLoad = r.GpuLoadIsTrue; // set first so the tooltip wording is right
            CpuGauge.LoadValue = r.CpuLoad;
            GpuGauge.LoadValue = r.GpuLoad;
            CpuGauge.PeakLoad = r.PeakCpuLoad;
            GpuGauge.PeakLoad = r.PeakGpuLoad;
            CpuGauge.Peak = r.PeakCpu;
            GpuGauge.Peak = r.PeakGpu;
        }
        else
        {
            CpuBar.Value = r.CpuTemp ?? double.NaN;
            GpuBar.Value = r.GpuTemp ?? double.NaN;
            GpuBar.TrueLoad = r.GpuLoadIsTrue;
            CpuBar.LoadValue = r.CpuLoad;
            GpuBar.LoadValue = r.GpuLoad;
            CpuBar.PeakLoad = r.PeakCpuLoad;
            GpuBar.PeakLoad = r.PeakGpuLoad;
            CpuBar.Peak = r.PeakCpu;
            GpuBar.Peak = r.PeakGpu;
        }

        TopStatus.Text = $"Case fans follow your hottest item - {r.Status}";
        TopStatus.Foreground = r.NoControllableFans || r.SentinelLost ? Res("Hot") : Res("TextDim");

        UpdateFanGauges(r);
    }

    // ---- actions ------------------------------------------------------------

    private void OnResetPeaksClick(object sender, RoutedEventArgs e)
        => _controller.ResetDisplayPeaks(); // every view clears on the next tick

    /// <summary>Peak Info: what set each peak marker.</summary>
    private void OnPeakInfoClick(object sender, RoutedEventArgs e) =>
        MessageWindow.Show(this, "What set your peaks", _controller.BuildPeakReport(),
                           copyButton: true, autoWidth: true);

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTitleBarColors();

    private void ApplyTitleBarColors()
    {
        if (!_titleBarReady) return;
        TitleBarColor.Apply(
            this,
            caption: ResColor("Panel"),
            text: ResColor("Text"),
            border: ResColor("PanelEdge"));
    }

    // ---- game mode ----------------------------------------------------------

    /// <summary>Show the overlay and hide (not close) the main window, which would stop the controller.</summary>
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

    // Minimise hides to the tray: a set-and-forget background app.
    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        Hide();
        WorkingSet.Trim();
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>The glyph shows what the button will do, not the current state.</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool max = WindowState == WindowState.Maximized;
        MaxButton.Content = max ? "" : "";   // Segoe MDL2: restore / maximise
        MaxButton.ToolTip = max ? "Restore" : "Maximise";

        // Minimized still counts as visible to WPF, so freeze the blades by hand.
        bool min = WindowState == WindowState.Minimized;
        foreach (FanBlade g in _fanGauges.Values) g.Hold = min;
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private Color ResColor(string key) => ((SolidColorBrush)FindResource(key)).Color;
}
