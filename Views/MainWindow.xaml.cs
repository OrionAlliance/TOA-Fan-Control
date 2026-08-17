using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private const int FansPerRow = 4;

    // Fan tile footprint. Smaller than the 200x195 temp dials - the fan graphic
    // shrinks to fit, but the % and label keep the control's minimum readable sizes
    // rather than scaling away to nothing.
    private const double FanTileW = 120;
    private const double FanTileH = 124;

    private readonly FanController _controller = App.Controller;
    private GameModeWindow? _overlay;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _titleBarReady;
    private bool _conflictNotified;

    // A spinning fan tile per running fan, created the first time that fan is seen
    // spinning and kept after (latched, so a momentary dip doesn't make it vanish).
    // Each fan also gets a bar - both display styles stay live, Settings just
    // picks which panel is visible. Fan bars carry a live "RPM: n" right after
    // the % (StatBar.TrailText) - the visible heartbeat that replaces the dial
    // view's spinning blades (a bar sitting still at a steady % otherwise reads
    // as "is this thing frozen?").
    private readonly Dictionary<string, FanBlade> _fanGauges = new();
    private readonly Dictionary<string, StatBar> _fanBars = new();
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

        ApplyDisplayStyle(_controller.Settings.DisplayStyle);

        // We draw the title bar ourselves, so the maximise glyph is kept in step
        // by hand. The DWM call still colours the window's outer border. Re-applied
        // whenever the theme flips, so the OS chrome follows the palette.
        StateChanged += OnWindowStateChanged;
        SourceInitialized += (_, _) => { _titleBarReady = true; ApplyTitleBarColors(); };
        ThemeManager.Changed += OnThemeChanged;

        _controller.Updated += OnUpdated;
        Closing += (_, _) =>
        {
            _controller.Updated -= OnUpdated;
            ThemeManager.Changed -= OnThemeChanged;

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

    /// <summary>Small non-modal tray notice - good news never needs a modal.</summary>
    public void ShowTrayBalloon(string title, string text) =>
        _tray?.ShowBalloonTip(8000, title, text, System.Windows.Forms.ToolTipIcon.Info);

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

        // The user came looking - surface any update they missed while hidden.
        _ = ((App)Application.Current).CheckOnUserReturnAsync();
    }

    private void OnUpdated(object? sender, FanReadings r) => Dispatcher.BeginInvoke(() => Render(r));

    private void Render(FanReadings r)
    {
        // Tray-facing updates come first - they matter even while hidden.
        // Live tray tooltip, so you can hover it while minimised and see the state
        // without reopening. NotifyIcon.Text caps at 63 chars - keep it short.
        if (_tray != null)
        {
            string hot = r.SourceTemp is { } t ? $"{t:F0}°C" : "--";
            _tray.Text = $"TOA - Fan Control  ·  {hot}  ·  fans {r.OutputPercent:F0}%";
        }

        // One balloon per conflict episode: the app is holding control, but the
        // real fix is turning fan control off in the other program.
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

        // Nobody's looking: while hidden in the tray, skip painting the dashboard
        // entirely - no dial animation, no bar redraws, no per-tick allocations.
        // Safe because the peaks live in the controller now, so the first tick
        // after reopening repaints everything current with nothing lost.
        if (!IsVisible) return;

        CpuGauge.Value = r.CpuTemp ?? double.NaN;
        GpuGauge.Value = r.GpuTemp ?? double.NaN;
        CpuBar.Value = r.CpuTemp ?? double.NaN;
        GpuBar.Value = r.GpuTemp ?? double.NaN;

        // Live load rides the cyan triangle; peaks come from the controller -
        // the same numbers in every view. Wording flag first, so the first
        // tooltip a marker ever gets already uses the right word.
        GpuGauge.TrueLoad = r.GpuLoadIsTrue;
        GpuBar.TrueLoad = r.GpuLoadIsTrue;
        CpuGauge.LoadValue = r.CpuLoad;
        GpuGauge.LoadValue = r.GpuLoad;
        CpuBar.LoadValue = r.CpuLoad;
        GpuBar.LoadValue = r.GpuLoad;
        CpuGauge.PeakLoad = r.PeakCpuLoad;
        GpuGauge.PeakLoad = r.PeakGpuLoad;
        CpuBar.PeakLoad = r.PeakCpuLoad;
        GpuBar.PeakLoad = r.PeakGpuLoad;
        CpuGauge.Peak = r.PeakCpu;
        GpuGauge.Peak = r.PeakGpu;
        CpuBar.Peak = r.PeakCpu;
        GpuBar.Peak = r.PeakGpu;

        TopStatus.Text = $"Case fans follow your hottest item - {r.Status}";
        TopStatus.Foreground = r.NoControllableFans || r.SentinelLost ? Res("Hot") : Res("TextDim");

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
                _fanBars[name] = new StatBar { Label = FanName.Display(name), Unit = "%" };
                _shownFans.Add(name);
                added = true;
            }

            _fanGauges[name].Value = rpm;             // real speed -> how fast it spins
            _fanGauges[name].Percent = r.OutputPercent; // driven duty -> the hub number

            _fanBars[name].Value = r.OutputPercent;
            _fanBars[name].TrailText = rpm is > 0 ? $"RPM: {rpm:F0}" : "RPM: --";
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

        // Fan bars pair up side by side, same as the CPU/GPU line above them.
        foreach (StatBar b in _fanBars.Values)
            (b.Parent as Panel)?.Children.Remove(b);

        FanBarRows.Children.Clear();
        for (int i = 0; i < _shownFans.Count; i += 2)
        {
            var row = new Grid();

            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            StatBar left = _fanBars[_shownFans[i]];
            left.Margin = new Thickness(0);
            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            if (i + 1 < _shownFans.Count)
            {
                StatBar right = _fanBars[_shownFans[i + 1]];
                right.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(right, 1);
                row.Children.Add(right);
            }
            // else: the odd one out keeps the left slot, same as reading order -
            // centred was tried and looked wrong ("ewww", 2026-07-23).

            FanBarRows.Children.Add(row);
        }
    }

    private static FanBlade NewFanGauge(string name) => new()
    {
        Label = FanName.Display(name),
        Width = FanTileW,
        Height = FanTileH,
    };

    // ---- actions ------------------------------------------------------------

    private void OnResetPeaksClick(object sender, RoutedEventArgs e)
        => _controller.ResetDisplayPeaks(); // every view clears on the next tick

    // ---- settings (the cog) --------------------------------------------------

    // When the menu is open, a click on the cog dismisses the menu FIRST (on the
    // press) and then fires the button (on the release) - which would reopen it
    // instantly, making the cog impossible to toggle shut. Closed fires with the
    // pointer still on the cog ONLY for that dismiss-press, so exactly that close
    // arms a one-shot swallow of the coming Click - no timers, no guessing.
    private bool _swallowNextSettingsClick;

    // Pressed the cog but released elsewhere: the armed swallow's Click never
    // comes, so leaving the button disarms it.
    private void OnSettingsMouseLeave(object sender, MouseEventArgs e)
        => _swallowNextSettingsClick = false;

    /// <summary>Rebuilt on every open so each header reflects the current state.</summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_swallowNextSettingsClick)
        {
            _swallowNextSettingsClick = false;
            return;
        }

        // Opens UPWARDS - the cog sits at the bottom edge of the window, so a
        // downward menu would hang off the app over the desktop.
        var menu = new ContextMenu
        {
            PlacementTarget = SettingsButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
        };
        menu.Closed += (_, _) => _swallowNextSettingsClick = SettingsButton.IsMouseOver;

        menu.Items.Add(Item(
            ThemeManager.Current == AppTheme.Dark ? "Switch to light theme" : "Switch to dark theme",
            ToggleTheme));

        menu.Items.Add(Item(
            BarsPanel.Visibility == Visibility.Visible ? "Switch to dial display" : "Switch to bar display",
            ToggleDisplayStyle));

        menu.Items.Add(Item(
            _controller.IsPaused ? "Take fans back" : "Hand fans to BIOS",
            ToggleBios));

        menu.Items.Add(Item("Choose fans…", ChooseFans));

        menu.Items.Add(Item(
            (StartupTask.IsEnabled() ? "✓  " : "") + "Start with Windows",
            ToggleStartup));

        menu.Items.Add(Item("Check for updates", CheckForUpdates));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("About", ShowAbout));
        menu.Items.Add(Item("Donate ♥", () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://ko-fi.com/orionailliance") { UseShellExecute = true })));
        menu.Items.Add(Item("Uninstall…", ConfirmUninstall));

        menu.IsOpen = true;

        static MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            return mi;
        }
    }

    private void ToggleTheme()
    {
        AppTheme next = ThemeManager.Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.Apply(next);
        _controller.UpdateSettings(s => s.Theme = next.ToString());
    }

    /// <summary>Dials or bars - same numbers, different dashboard. Saved like the theme.</summary>
    private void ToggleDisplayStyle()
    {
        string next = BarsPanel.Visibility == Visibility.Visible ? "Dials" : "Bars";
        ApplyDisplayStyle(next);
        _controller.UpdateSettings(s => s.DisplayStyle = next, reresolve: false);
    }

    private void ApplyDisplayStyle(string style)
    {
        bool bars = string.Equals(style, "Bars", StringComparison.OrdinalIgnoreCase);
        BarsPanel.Visibility = bars ? Visibility.Visible : Visibility.Collapsed;
        DialsPanel.Visibility = bars ? Visibility.Collapsed : Visibility.Visible;

        // SizeToContent only recalculates on content changes while Normal; a
        // maximised window keeps its size either way.
        if (WindowState == WindowState.Normal)
        {
            InvalidateMeasure();
        }
    }

    /// <summary>Pause/resume: hand the fans back to the BIOS, or take them again.</summary>
    private void ToggleBios()
    {
        if (_controller.IsPaused) _controller.Resume();
        else _controller.Pause();
    }

    private void ShowAbout() => new AboutWindow { Owner = this }.ShowDialog();

    /// <summary>Manual check. Silence would read as broken, so "current" says so.</summary>
    private async void CheckForUpdates()
    {
        // A flaky connection can take ~30s before anything shows - a wait cursor
        // over THIS window says "working" (window-scoped, so any update dialog
        // that opens mid-check keeps its normal arrow).
        Cursor = Cursors.Wait;
        bool offered;
        try { offered = await ((App)Application.Current).CheckForUpdatesNowAsync(); }
        finally { Cursor = null; }
        if (offered) return;

        Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                    ?? new Version(0, 0, 0);
        MessageWindow.Show(this, "You're up to date.",
            $"App v{v.Major}.{v.Minor}.{v.Build}, PawnIO, and .NET are all current.");
    }

    /// <summary>Register/unregister the logon task. Menu shows fresh state next open.</summary>
    private void ToggleStartup()
    {
        if (StartupTask.IsEnabled()) StartupTask.Disable();
        else if (!StartupTask.Enable())
            MessageWindow.Show(this, "Couldn't register the startup task",
                "Windows refused the scheduled task. Details are in fan_debug.log, " +
                "next to the app.");
    }

    /// <summary>
    /// Re-pick which fans to drive. Applies NEXT launch on purpose: the watchdog
    /// seized the current set at startup and only it holds their BIOS state - a
    /// fan added mid-run would be driven unguarded.
    /// </summary>
    private void ChooseFans()
    {
        var picker = new FanPickerWindow(
            _controller.CandidateFans, _controller.Settings.SelectedFans, firstRun: false)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        picker.ShowDialog();

        if (picker.Selection == null) return; // cancelled
        _controller.UpdateSettings(s => s.SelectedFans = picker.Selection, reresolve: false);
        DebugLog.Write("Fan selection changed - applies next launch.");
    }

    private void ConfirmUninstall()
    {
        bool yes = MessageWindow.Confirm(this,
            "Uninstall TOA - Fan Control?",
            "This will close the app and remove it from this PC - the app, its " +
            "settings, its log, and its shortcuts.\n\n" +
            "(PawnIO and .NET stay: they're shared system components other software " +
            "can use.)",
            "Uninstall", "Cancel");

        if (yes) Uninstaller.Run();
    }

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
