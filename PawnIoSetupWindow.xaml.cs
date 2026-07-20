using System.Windows;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

/// <summary>
/// Shown when PawnIO is missing (first-run install) or out of date (update). Same
/// download-verify-install pipeline either way; the update path just reworded the
/// text and points at the newer version. Declining leaves things as they are.
/// </summary>
public partial class PawnIoSetupWindow : Window
{
    /// <summary>True once PawnIO is installed (a reboot may still be pending).</summary>
    public bool Installed { get; private set; }

    /// <summary>The installer asked for a reboot to finish.</summary>
    public bool RebootRequired { get; private set; }

    public PawnIoSetupWindow()
    {
        InitializeComponent();
    }

    /// <summary>Update-available variant: PawnIO is already installed but behind.</summary>
    public PawnIoSetupWindow(PawnIoSetup.UpdateInfo update)
    {
        InitializeComponent();

        Title = "TOA - Fan Control · Update";
        HeaderText.Text = "PawnIO update available";
        BodyText.Text =
            $"A newer version of the PawnIO driver is available.\n\n" +
            $"Installed:  {update.Installed}\nLatest:  {update.Latest}";
        SubText.Text =
            "The app will download it straight from its author's official release and " +
            "check its signature before running it. Nothing changes without your OK.";
        InstallButton.Content = "Update PawnIO";
        LaterButton.Content = "Not now";
        HintText.Text = "You can keep using the current version if you'd rather not.";
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Visible;
        Progress.Visibility = Visibility.Visible;

        var progress = new Progress<string>(s => StatusText.Text = s);
        PawnIoSetup.InstallResult result = await PawnIoSetup.DownloadVerifyInstallAsync(progress);

        Progress.Visibility = Visibility.Collapsed;
        StatusText.Text = result.Message;

        if (result.Success)
        {
            Installed = true;
            RebootRequired = result.RebootRequired;
            DebugLog.Write($"PawnIO install succeeded (rebootRequired={result.RebootRequired}).");

            InstallButton.Content = "Continue";
            InstallButton.IsEnabled = true;
            InstallButton.Click -= OnInstallClick;
            InstallButton.Click += (_, _) => Close();
        }
        else
        {
            // Let them retry or bail out.
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
    }

    private void OnLaterClick(object sender, RoutedEventArgs e)
    {
        Installed = false;
        Close();
    }
}
