using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FanControlApp.Helpers;

namespace FanControlApp;

/// <summary>One fan row in the FANS strip. Display shape only.</summary>
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
    private readonly FanController _controller = App.Controller;
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
            TempSource.Cpu => 1,
            TempSource.Gpu => 2,
            _ => 0,
        };

        ApplyModeVisibility(s.Mode);

        _controller.Updated += OnUpdated;
        Closing += (_, _) =>
        {
            _controller.Updated -= OnUpdated;
            _controller.Dispose();
        };

        _ready = true;
    }

    private void OnUpdated(object? sender, FanReadings r)
    {
        Dispatcher.BeginInvoke(() => Render(r));
    }

    private void Render(FanReadings r)
    {
        CpuTempText.Text = Fmt(r.CpuTemp);
        CpuTempText.Foreground = TempBrush(r.CpuTemp);
        CpuTempName.Text = _controller.Hardware.CpuTempName;

        GpuTempText.Text = Fmt(r.GpuTemp);
        GpuTempText.Foreground = TempBrush(r.GpuTemp);
        GpuTempName.Text = _controller.Hardware.GpuTempName;

        OutputText.Text = $"{r.OutputPercent:F0}%";
        SourceText.Text = r.SourceTemp is { } t ? $"driving from {t:F1} C" : "no reading";

        EngagedBadge.Text = r.Engaged ? "App is driving the case fans" : "BIOS has the fans";
        EngagedBadge.Foreground = r.Engaged ? Res("Accent") : Res("TextDim");

        StatusText.Text = r.Status;
        StatusText.Foreground = r.Panic || r.NoControllableFans ? Res("Hot") : Res("TextDim");

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

        RenderFans(r);
    }

    private void RenderFans(FanReadings r)
    {
        List<string> driven = _controller.Settings.ControlledFans;

        FanList.ItemsSource = r.Fans
            .Where(f => f.RpmSensor != null)
            .Select(f =>
            {
                bool isDriven = driven.Contains(f.Name, StringComparer.OrdinalIgnoreCase);
                bool dead = f.Rpm is null or < 1;

                return new FanRow
                {
                    Name = isDriven ? f.Name + "  (app)" : f.Name,
                    Reading = dead ? "--" : $"{f.Rpm:F0} rpm",
                    NameBrush = isDriven ? Res("Accent") : Res("TextDim"),
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
            1 => TempSource.Cpu,
            2 => TempSource.Gpu,
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

    private void OnReleaseClick(object sender, RoutedEventArgs e)
    {
        _controller.SafeRelease();
        StatusText.Text = "Released - the BIOS curve has the fans back.";
    }

    // ---- display helpers ----------------------------------------------------

    private static string Fmt(float? v) => v is { } f ? $"{f:F0} C" : "--";

    private Brush Res(string key) => (Brush)FindResource(key);

    private Brush TempBrush(float? v) => v switch
    {
        null => Res("TextDim"),
        < 60 => Res("Cool"),
        < 78 => Res("Warm"),
        _ => Res("Hot"),
    };
}
