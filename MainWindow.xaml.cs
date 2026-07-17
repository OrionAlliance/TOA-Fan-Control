using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FanControlApp.Helpers;

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
/// Display only - it renders what the controller reports and forwards the user's
/// choices straight back to it. No fan logic lives here.
/// </summary>
public partial class MainWindow : Window
{
    private const string Fan2 = "Chassis Fan #2";
    private const string Fan3 = "Chassis Fan #3";

    private readonly FanController _controller = App.Controller;
    private GameModeWindow? _overlay;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();

        FanSettings s = _controller.Settings;

        Curve.Curve = s.Curve;
        Curve.CurveChanged += OnCurveChanged;

        ManualRadio.IsChecked = s.Mode == FanMode.Manual;
        AutoRadio.IsChecked = s.Mode == FanMode.Auto;
        TargetSlider.Value = s.TargetTemp;
        TargetText.Text = $"{s.TargetTemp:F0} C";
        SourceCombo.SelectedIndex = s.Source switch
        {
            TempSource.Cpu => 0,
            TempSource.Gpu => 1,
            _ => 2,
        };

        // The redline on the temp gauges is the real one: the 5800X throttles at
        // 90C. Nothing below that is damage.
        CpuGauge.RedFrom = 90;
        GpuGauge.RedFrom = 90;

        ApplyModeVisibility(s.Mode);

        // We draw the title bar ourselves now (DWM can't gradient one), so the
        // maximise glyph has to be kept in step by hand.
        StateChanged += OnWindowStateChanged;

        // Still worth doing for the window's outer border - that edge is DWM's
        // even when the caption isn't.
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

            _controller.Dispose();
        };

        _ready = true;
    }

    private void OnUpdated(object? sender, FanReadings r) => Dispatcher.BeginInvoke(() => Render(r));

    private void Render(FanReadings r)
    {
        CpuGauge.Value = r.CpuTemp ?? double.NaN;
        GpuGauge.Value = r.GpuTemp ?? double.NaN;
        Fan2Gauge.Value = RpmOf(r, Fan2);
        Fan3Gauge.Value = RpmOf(r, Fan3);

        EngagedBadge.Text = r.Engaged ? "App is driving the case fans" : "BIOS has the fans";
        EngagedBadge.Foreground = r.Engaged ? Res("Accent") : Res("TextDim");
        UpdateReleaseButton();

        StatusText.Text = $"Fans at {r.OutputPercent:F0}%  ·  {r.Status}";
        StatusText.Foreground = r.Panic || r.NoControllableFans || r.SentinelLost
            ? Res("Hot")
            : Res("TextDim");

        Curve.SetLive(r.SourceTemp, r.OutputPercent);

        if (r.Mode == FanMode.Auto && r.SourceTemp is { } temp)
        {
            float delta = temp - _controller.Settings.TargetTemp;
            AutoStateText.Text = MathF.Abs(delta) < 1.5f
                ? $"Settled - holding at {temp:F1} C with the fans at {r.OutputPercent:F0}%."
                : delta > 0
                    ? $"{delta:F1} C over target - ramping up (now {r.OutputPercent:F0}%)."
                    : $"{-delta:F1} C under target - easing off (now {r.OutputPercent:F0}%).";
        }

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

    // ---- user input, forwarded to the controller ----------------------------

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        FanMode mode = AutoRadio.IsChecked == true ? FanMode.Auto : FanMode.Manual;
        _controller.UpdateSettings(s => s.Mode = mode);
        ApplyModeVisibility(mode);
        DebugLog.Write($"Mode -> {mode}");
    }

    private void ApplyModeVisibility(FanMode mode)
    {
        ManualPanel.Visibility = mode == FanMode.Manual ? Visibility.Visible : Visibility.Collapsed;
        AutoPanel.Visibility = mode == FanMode.Auto ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;

        TempSource src = SourceCombo.SelectedIndex switch
        {
            0 => TempSource.Cpu,
            1 => TempSource.Gpu,
            _ => TempSource.Hotter,
        };
        _controller.UpdateSettings(s => s.Source = src);
    }

    private void OnTargetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;

        var target = (float)e.NewValue;
        TargetText.Text = $"{target:F0} C";
        _controller.UpdateSettings(s => s.TargetTemp = target);
    }

    private void OnCurveChanged(object? sender, EventArgs e)
    {
        if (!_ready) return;
        _controller.UpdateSettings(s => s.Curve = Curve.Curve);
    }

    /// <summary>
    /// A real toggle. It used to be a one-shot "release" that the very next tick
    /// silently undid by grabbing the fans straight back - it looked like it
    /// worked and lasted under a second.
    /// </summary>
    private void OnReleaseClick(object sender, RoutedEventArgs e)
    {
        if (_controller.IsPaused) _controller.Resume();
        else _controller.Pause();

        UpdateReleaseButton();
    }

    private void UpdateReleaseButton() =>
        ReleaseButton.Content = _controller.IsPaused ? "Take fans back" : "Hand fans to BIOS";

    // ---- caption buttons ----------------------------------------------------
    // Ours now: DWM can't gradient a title bar, so we draw it, so we own these.

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

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

    private Brush Res(string key) => (Brush)FindResource(key);

    private Color ResColor(string key) => ((SolidColorBrush)FindResource(key)).Color;
}
