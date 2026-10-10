using System.Windows;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

/// <summary>Download-verify-install popup for PawnIO (missing or outdated), app, and .NET updates.</summary>
public partial class PawnIoSetupWindow : Window
{
    /// <summary>True once PawnIO is installed (a reboot may still be pending).</summary>
    public bool Installed { get; private set; }

    /// <summary>The installer asked for a reboot to finish.</summary>
    public bool RebootRequired { get; private set; }

    /// <summary>Update installed; the orchestrator restarts once after all checks, not per dialog.</summary>
    public bool RestartWanted { get; private set; }

    // What the Install/Update button runs: PawnIO by default, swapped by the other variants.
    private Func<IProgress<string>, Task<PawnIoSetup.InstallResult>> _installer =
        PawnIoSetup.DownloadVerifyInstallAsync;

    // Updates restart the app so they take effect now; first-run install keeps its Continue flow.
    private bool _restartAppOnSuccess;

    // The app updater closes the app on its own, so its success screen needs no button.
    private bool _closesItself;

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
        HintText.Text = "The app restarts itself when the update finishes. Or keep the current version - your call.";
        _restartAppOnSuccess = true;
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
        _closesItself = true;
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
        HintText.Text = "The app restarts itself when the update finishes, so it takes effect right away.";
        _restartAppOnSuccess = true;
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        // Choice made: the popup becomes a progress window with nothing to click.
        ButtonRow.Visibility = Visibility.Collapsed;
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
            DebugLog.Write($"Update installed (rebootRequired={result.RebootRequired}).");

            if (_restartAppOnSuccess && !result.RebootRequired)
            {
                RestartWanted = true;
                Close();
                return;
            }

            if (_closesItself) return;

            // First run, or reboot pending so a restart is pointless: show the result behind Continue.
            InstallButton.Content = "Continue";
            InstallButton.Click -= OnInstallClick;
            InstallButton.Click += (_, _) => Close();
            LaterButton.Visibility = Visibility.Collapsed;
            ButtonRow.Visibility = Visibility.Visible;
        }
        else
        {
            // Let them retry or bail out.
            ButtonRow.Visibility = Visibility.Visible;
        }
    }

    private void OnLaterClick(object sender, RoutedEventArgs e) => Close();
}
