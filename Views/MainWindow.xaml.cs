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
        Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                    ?? new Version(0, 0, 0);
        string label = $"TOA - Fan Control  v{v.Major}.{v.Minor}.{v.Build}";
        TitleText.Text = label;
        Title = label;

        SetupTray();

        // Redline at the real throttle point (90C); nothing below it is damage.
        CpuGauge.RedFrom = 90;
        GpuGauge.RedFrom = 90;

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

    // ---- system tray --------------------------------------------------------

    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            // The exe's embedded icon, so the tray matches the app.
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

    /// <summary>Small non-modal tray notice.</summary>
    public void ShowTrayBalloon(string title, string text) =>
        _tray?.ShowBalloonTip(8000, title, text, System.Windows.Forms.ToolTipIcon.Info);

    private void ShowFromTray()
    {
        // In Game Mode, leave it properly instead of stacking behind the overlay.
        if (_overlay is { IsVisible: true })
        {
            LeaveGameMode();
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();

        // Surface any update missed while hidden.
        _ = ((App)Application.Current).CheckOnUserReturnAsync();
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

    /// <summary>One dial per driven fan, latched once seen spinning so empty headers never show.</summary>
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
                if (rpm is not (> 0)) continue; // not spinning yet
                FanBlade g = NewFanGauge(name);
                _fanGauges[name] = g;
                _fanBars[name] = new StatBar { Label = FanName.Display(name), Unit = "%" };
                _shownFans.Add(name);
                added = true;
            }

            _fanGauges[name].Value = rpm;             // spin speed
            _fanGauges[name].Percent = r.OutputPercent; // hub number

            _fanBars[name].Value = r.OutputPercent;
            _fanBars[name].TrailText = rpm is > 0 ? $"RPM: {rpm:F0}" : "RPM: --";
        }

        if (added) RebuildFanRows();
    }

    /// <summary>Re-lay the shown fans into centred dial rows and paired bar rows.</summary>
    private void RebuildFanRows()
    {
        // Detach tiles first; WPF throws when adding a control that still has a parent.
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

        // Fan bars pair up side by side, like the CPU/GPU line.
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
            // An odd last fan keeps the left slot, in reading order.

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

    // A cog click while open closes the menu on press, so swallow the Click that would reopen it.
    private bool _swallowNextSettingsClick;

    // Released elsewhere, so the swallowed Click never comes: disarm on leave.
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

        // Opens upward, since the cog sits at the window's bottom edge.
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

    /// <summary>Toggle dials or bars and save the choice.</summary>
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

        // SizeToContent only recalculates while Normal.
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

    /// <summary>Peak Info: what set each peak marker.</summary>
    private void OnPeakInfoClick(object sender, RoutedEventArgs e) =>
        MessageWindow.Show(this, "What set your peaks", _controller.BuildPeakReport(),
                           copyButton: true, autoWidth: true);

    private void ShowAbout() => new AboutWindow { Owner = this }.ShowDialog();

    /// <summary>Manual check. Silence would read as broken, so "current" says so.</summary>
    private async void CheckForUpdates()
    {
        // Window-scoped wait cursor for slow checks; update dialogs keep the normal arrow.
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

    /// <summary>Register or unregister the logon task.</summary>
    private void ToggleStartup()
    {
        if (StartupTask.IsEnabled()) StartupTask.Disable();
        else if (!StartupTask.FolderIsSafe())
            MessageWindow.Show(this, "Start with Windows needs the installer",
                "This copy isn't in a protected folder, so Windows can't safely start it as admin. " +
                "Install it with the setup from the official Releases page, then turn this on.");
        else if (!StartupTask.Enable())
            MessageWindow.Show(this, "Couldn't register the startup task",
                "Windows refused the scheduled task. Details are in fan_debug.log, " +
                "next to the app.");
    }

    /// <summary>Re-pick fans; applies next launch so the watchdog guards every driven fan's BIOS state.</summary>
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
        IReadOnlyList<(string Name, float? Rpm)> candidates = _controller.CandidateFans;
        _controller.UpdateSettings(s =>
        {
            s.SelectedFans = picker.Selection;
            NewFans.RecordDecisions(s, candidates);
        }, reresolve: false);
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
