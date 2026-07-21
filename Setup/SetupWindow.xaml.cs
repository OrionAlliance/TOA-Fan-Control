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

            // ---- the app ----
            SetStatus("Installing TOA - Fan Control…");
            ShowProgress(true);
            AppInstaller.ExtractApp();
            AppInstaller.CreateShortcut();

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

    // ---- ui helpers ---------------------------------------------------------

    private void SetStatus(string text)
    {
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xC2, 0xCA, 0xD9));
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

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_choice != null)
        {
            HideButtons();
            _choice.TrySetResult(true);
        }
        else Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        if (_choice != null)
        {
            HideButtons();
            _choice.TrySetResult(false);
        }
        else Close();
    }

    private void HideButtons()
    {
        _choice = null;
        Buttons.Visibility = Visibility.Collapsed;
        PrimaryButton.Visibility = Visibility.Collapsed;
        SecondaryButton.Visibility = Visibility.Collapsed;
    }
}
