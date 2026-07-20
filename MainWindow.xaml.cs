using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FanControlApp.Controls;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

/// <summary>
/// Display only - it renders what the controller reports and forwards the two
/// actions (pause, Game Mode) back to it. No fan logic lives here, and there's
/// nothing to configure: the app just matches the fans to the hottest part.
/// </summary>
public partial class MainWindow : Window
{
    private const int FansPerRow = 3;

    private readonly FanController _controller = App.Controller;
    private GameModeWindow? _overlay;
    private System.Windows.Forms.NotifyIcon? _tray;

    // A spinning fan tile per running fan, created the first time that fan is seen
    // spinning and kept after (latched, so a momentary dip doesn't make it vanish).
    private readonly Dictionary<string, FanBlade> _fanGauges = new();
    private readonly List<string> _shownFans = new();

    public MainWindow()
    {
        InitializeComponent();

        // Stamp the real build version into the caption (and the taskbar title), read
        // from the assembly so it tracks <Version> in the csproj and never goes stale.
        Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                    ?? new Version(0, 0, 0);
        string label = $"TOA - Fan Control  v{v.Major}.{v.Minor}.{v.Build}";
        TitleText.Text = label;
        Title = label;

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

        TopStatus.Text = $"Case fans follow your hottest item - {r.Status}";
        TopStatus.Foreground = r.NoControllableFans || r.SentinelLost ? Res("Hot") : Res("TextDim");

        // Live tray tooltip, so you can hover it while minimised and see the state
        // without reopening. NotifyIcon.Text caps at 63 chars - keep it short.
        if (_tray != null)
        {
            string hot = r.SourceTemp is { } t ? $"{t:F0}°C" : "--";
            _tray.Text = $"TOA Fan Control  ·  {hot}  ·  fans {r.OutputPercent:F0}%";
        }

        UpdateReleaseButton();
        UpdateFanGauges(r);
    }

    /// <summary>
    /// One dial per driven fan that's actually spinning. A fan appears the first
    /// time it's seen above 0 RPM and stays (latched) - so empty headers never
    /// show, and a real fan doesn't flicker away on a momentary dip.
    /// </summary>
    private void UpdateFanGauges(FanReadings r)
    {
        bool added = false;

        foreach (string name in r.DrivenFans)
        {
            FanChannel? f = r.Fans.FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            double rpm = f?.Rpm ?? double.NaN;

            if (!_fanGauges.ContainsKey(name))
            {
                if (rpm is not (> 0)) continue; // not spinning yet - empty header or stopped
                FanBlade g = NewFanGauge(name);
                _fanGauges[name] = g;
                _shownFans.Add(name);
                added = true;
            }

            _fanGauges[name].Value = rpm;             // real speed -> how fast it spins
            _fanGauges[name].Percent = r.OutputPercent; // driven duty -> the hub number
        }

        if (added) RebuildFanRows();
    }

    /// <summary>Re-lay the shown fans into centred rows of three.</summary>
    private void RebuildFanRows()
    {
        // Detach every tile from its old row first - WPF throws if you add a
        // control that still has a parent.
        foreach (FanBlade g in _fanGauges.Values)
            (g.Parent as Panel)?.Children.Remove(g);

        FanRows.Children.Clear();

        for (int i = 0; i < _shownFans.Count; i += FansPerRow)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            for (int j = i; j < System.Math.Min(i + FansPerRow, _shownFans.Count); j++)
                row.Children.Add(_fanGauges[_shownFans[j]]);

            FanRows.Children.Add(row);
        }
    }

    private static FanBlade NewFanGauge(string name) => new()
    {
        Label = name,
        Width = 200,
        Height = 195,
    };

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
