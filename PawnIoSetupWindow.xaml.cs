using System.Windows;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

/// <summary>
/// First-run gate shown only when PawnIO is missing. Offers a one-click install;
/// declining lets the app open in read-only mode.
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
