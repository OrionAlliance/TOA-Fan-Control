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

    // What the Install/Update button actually runs. PawnIO by default; the .NET
    // update reuses this same window with its own installer plugged in.
    private Func<IProgress<string>, Task<PawnIoSetup.InstallResult>> _installer =
        PawnIoSetup.DownloadVerifyInstallAsync;

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

    /// <summary>App-update variant: downloads the latest GitHub release installer.</summary>
    public PawnIoSetupWindow(AppUpdate.UpdateInfo update)
    {
        InitializeComponent();
        _installer = AppUpdate.InstallerFor(update);

        Title = "TOA - Fan Control · Update";
        HeaderText.Text = "App update available";
        BodyText.Text =
            $"A newer version of TOA - Fan Control is available.\n\n" +
            $"Installed:  v{update.Installed}\nLatest:  v{update.Latest}";
        SubText.Text =
            "The update downloads from the app's official GitHub releases. The app " +
            "will close and the installer finishes the job - your settings and fan " +
            "selection are kept.";
        InstallButton.Content = "Update app";
        LaterButton.Content = "Not now";
        HintText.Text = "Every past version stays downloadable on GitHub if you ever want to roll back.";
    }

    /// <summary>.NET-update variant: same window, Microsoft's runtime installer.</summary>
    public PawnIoSetupWindow(DotNetUpdate.UpdateInfo update)
    {
        InitializeComponent();
        _installer = DotNetUpdate.InstallAsync;

        Title = "TOA - Fan Control · Update";
        HeaderText.Text = ".NET update available";
        BodyText.Text =
            $"A newer version of .NET 10 is available. Microsoft ships security and " +
            $"performance fixes this way.\n\n" +
            $"Installed:  {update.Installed}\nLatest:  {update.Latest}";
        SubText.Text =
            "The app will download it straight from Microsoft and check its signature " +
            "before running it. Nothing changes without your OK.";
        InstallButton.Content = "Update .NET";
        LaterButton.Content = "Not now";
        HintText.Text = "The update takes effect the next time the app starts.";
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Visible;
        Progress.Visibility = Visibility.Visible;

        var progress = new Progress<string>(s => StatusText.Text = s);
        PawnIoSetup.InstallResult result = await _installer(progress);

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
