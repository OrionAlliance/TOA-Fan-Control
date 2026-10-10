// The cog settings menu and its actions.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FanControlApp.Controls;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

public partial class MainWindow
{
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
        menu.Items.Add(Item("Donate ♥", () => Browser.Open("https://ko-fi.com/orionailliance")));
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

        MessageWindow.Show(this, "You're up to date.",
            $"App {AppVersion.Display}, PawnIO, and .NET are all current.");
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
        if (Uninstaller.Confirm(this, appIsRunning: true)) Uninstaller.Run();
    }
}
