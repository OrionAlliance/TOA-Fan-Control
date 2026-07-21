using System.IO;
using System.Windows;
using System.Windows.Media;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlSetup;

/// <summary>
/// The whole installer, start to finish:
///   1. .NET 10 present? If not, install it (required - the app can't run without it).
///   2. PawnIO present? If not, ask. Decline = stop and close (required too).
///   3. Write the app + a Start-menu shortcut, then launch it.
/// Every step says what it's doing; either prerequisite can stop the whole thing.
/// </summary>
public partial class SetupWindow : Window
{
    private TaskCompletionSource<bool>? _choice;
    private bool _rebootNeeded;

    public SetupWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        DebugLog.Write(new string('=', 60));
        DebugLog.Write("Setup starting.");

        try
        {
            var progress = new Progress<string>(SetStatus);

            // ---- .NET 10 (required) ----
            SetStatus("Checking for .NET 10…");
            if (!DotNetUpdate.IsInstalled())
            {
                PawnIoSetup.InstallResult r = await DotNetUpdate.InstallAsync(progress);
                if (!r.Success) { Fail(r.Message); return; }
                _rebootNeeded |= r.RebootRequired;
            }
            else DebugLog.Write(".NET 10 already present.");

            // ---- PawnIO (required) ----
            SetStatus("Checking for the PawnIO driver…");
            if (!PawnIoSetup.IsInstalled())
            {
                bool yes = await AskAsync(
                    "PawnIO driver needed",
                    "TOA - Fan Control needs the PawnIO driver to read your temperatures and " +
                    "drive your fans. It's downloaded straight from its author's official, signed " +
                    "release. Install it now?",
                    "Install PawnIO", "Quit");

                if (!yes)
                {
                    DebugLog.Write("PawnIO declined - setup cancelled.");
                    ShowClose("PawnIO is required, so setup can't continue.");
                    return;
                }

                ShowProgress(true);
                PawnIoSetup.InstallResult pr = await PawnIoSetup.DownloadVerifyInstallAsync(progress);
                if (!pr.Success) { Fail(pr.Message); return; }
                _rebootNeeded |= pr.RebootRequired;
            }
            else DebugLog.Write("PawnIO already present.");

            // ---- where should it go? ----
            // A visible folder the user picked beats AppData every time - one
            // obvious place that IS the whole app (exe, settings, log together).
            string? installDir = await AskInstallDirAsync();
            if (installDir == null)
            {
                DebugLog.Write("Install location declined - setup cancelled.");
                ShowClose("Setup cancelled - the app wasn't installed.");
                return;
            }
            AppInstaller.InstallDir = installDir;
            DebugLog.Write($"Install location: {installDir}");

            // ---- the app ----
            SetStatus("Installing TOA - Fan Control…");
            ShowProgress(true);
            AppInstaller.ExtractApp();
            AppInstaller.CreateStartMenuShortcut();

            // The desktop is the user's space - that one gets asked about.
            bool desktop = await AskAsync(
                "Add a desktop shortcut?",
                "TOA - Fan Control is installed and in your Start menu. Want a " +
                "desktop shortcut too?",
                "Add it", "No thanks");

            if (desktop) AppInstaller.CreateDesktopShortcut();
            else DebugLog.Write("Desktop shortcut skipped by user.");

            // ---- done ----
            if (_rebootNeeded)
            {
                ShowClose("Installed. Restart your PC to finish, then open TOA - Fan Control " +
                          "from the Start menu.");
                return;
            }

            SetStatus("Starting TOA - Fan Control…");
            AppInstaller.Launch();
            DebugLog.Write("Setup complete - app launched.");
            Close();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Setup failed.", ex);
            Fail("Something went wrong during setup: " + ex.Message);
        }
    }

    /// <summary>
    /// Show the location box (default C:\TOA - Fan Control), let them edit or
    /// Browse, and create the folder. Re-asks on a bad path; null = they quit.
    /// </summary>
    private async Task<string?> AskInstallDirAsync()
    {
        PathBox.Text = AppInstaller.DefaultInstallDir;
        PathRow.Visibility = Visibility.Visible;

        string body = "Pick where TOA - Fan Control should live. Everything the app " +
                      "uses (settings, debug log) stays in this one folder.";

        while (true)
        {
            bool install = await AskAsync("Where should the app go?", body, "Install", "Quit");
            if (!install)
            {
                PathRow.Visibility = Visibility.Collapsed;
                return null;
            }

            string path = PathBox.Text.Trim();
            try
            {
                if (!Path.IsPathRooted(path))
                    throw new InvalidOperationException(@"use a full path, like C:\TOA - Fan Control");

                Directory.CreateDirectory(path);
                PathRow.Visibility = Visibility.Collapsed;
                return path;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Install path '{path}' rejected.", ex);
                body = $"Can't use that folder ({ex.Message.TrimEnd('.')}). Pick another:";
            }
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Pick where TOA - Fan Control should live",
            UseDescriptionForTitle = true,
        };

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        // If they picked a generic spot (a drive root, an Apps folder), give the
        // app its own folder inside it rather than dumping files loose.
        string p = dlg.SelectedPath;
        if (!string.Equals(Path.GetFileName(p.TrimEnd('\\')), AppInstaller.AppName,
                StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(p, AppInstaller.AppName);

        PathBox.Text = p;
    }

    // ---- ui helpers ---------------------------------------------------------

    private void SetStatus(string text)
    {
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        StatusText.Text = text;
    }

    private void ShowProgress(bool on) =>
        Progress.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Show a two-button question and wait for the answer.</summary>
    private Task<bool> AskAsync(string header, string body, string primary, string secondary)
    {
        HeaderText.Text = header;
        SetStatus(body);
        ShowProgress(false);

        PrimaryButton.Content = primary;
        SecondaryButton.Content = secondary;
        PrimaryButton.Visibility = Visibility.Visible;
        SecondaryButton.Visibility = Visibility.Visible;
        Buttons.Visibility = Visibility.Visible;

        _choice = new TaskCompletionSource<bool>();
        return _choice.Task;
    }

    /// <summary>Dead-end message with a single Close button (error or reboot-needed).</summary>
    private void ShowClose(string message)
    {
        _choice = null;
        ShowProgress(false);
        SetStatus(message);

        PrimaryButton.Content = "Close";
        PrimaryButton.Visibility = Visibility.Visible;
        SecondaryButton.Visibility = Visibility.Collapsed;
        Buttons.Visibility = Visibility.Visible;
    }

    private void Fail(string message)
    {
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
        _choice = null;
        ShowProgress(false);
        StatusText.Text = message;

        PrimaryButton.Content = "Close";
        PrimaryButton.Visibility = Visibility.Visible;
        SecondaryButton.Visibility = Visibility.Collapsed;
        Buttons.Visibility = Visibility.Visible;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => Answer(true);

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => Answer(false);

    private void Answer(bool yes)
    {
        // Grab the pending question BEFORE HideButtons clears it - answering
        // through the field after that was a guaranteed null-reference crash.
        TaskCompletionSource<bool>? choice = _choice;

        if (choice == null)
        {
            Close(); // no question pending - the button is a plain Close
            return;
        }

        HideButtons();
        choice.TrySetResult(yes);
    }

    private void HideButtons()
    {
        _choice = null;
        Buttons.Visibility = Visibility.Collapsed;
        PrimaryButton.Visibility = Visibility.Collapsed;
        SecondaryButton.Visibility = Visibility.Collapsed;
    }
}
