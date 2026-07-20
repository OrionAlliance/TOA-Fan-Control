using System.Windows;
using System.Windows.Threading;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

public partial class App : Application
{
    public static FanController Controller { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Second instance of this exe, running as the sentinel. No UI, no
        // controller - it just waits for the main app to die and releases.
        if (e.Args.Length > 0 && e.Args[0] == Watchdog.Flag)
        {
            StartAsWatchdog(e.Args);
            return;
        }

        DebugLog.Write(new string('=', 60));
        DebugLog.Write("TOA - Fan Control starting.");

        FanSettings settings = SettingsStore.Load();

        // Without PawnIO the app is blind to every fan, so offer to set it up
        // before we open the hardware. Declining just opens the app read-only.
        if (!PawnIoSetup.IsInstalled())
        {
            DebugLog.Write("PawnIO not installed - showing first-run setup.");
            var setup = new PawnIoSetupWindow();
            setup.ShowDialog();

            if (setup.Installed && setup.RebootRequired)
            {
                MessageBox.Show(
                    "PawnIO is installed, but Windows needs a reboot to finish loading it.\n\n" +
                    "Reboot, then open TOA - Fan Control again.",
                    "TOA - Fan Control", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        Controller = new FanController(settings);

        // Every path out of this process must give the fans back to the BIOS.
        // A seized header stuck at a low percent is the one way this app can do
        // real damage, so the release is wired to all of them.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Controller.Dispose();
        SessionEnding += OnSessionEnding;

        try
        {
            Controller.OpenHardware();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Controller failed to start.", ex);
            MessageBox.Show(
                "Couldn't open the fan hardware.\n\n" +
                "This app needs to run as administrator to reach the motherboard's " +
                "fan chip.\n\n" + ex.Message,
                "TOA - Fan Control", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // The watchdog must take the fans BEFORE we write to any of them: whoever
        // grabs a header first is the only one holding its real BIOS settings, and
        // therefore the only one that can ever hand it back. Blocking here is the
        // whole point - if we got in first, a force-kill would strand the fans.
        WatchdogLink? link = Watchdog.LaunchAndWait(
            Controller.ControlledFanNames, TimeSpan.FromSeconds(20));

        Controller.AttachWatchdog(link);
        Controller.BeginControl();

        new MainWindow().Show();

        // Once the app is up, quietly ask GitHub whether PawnIO has a newer version.
        // Never blocks startup; offline just no-ops. If there's one, a popup offers
        // it - Yes updates, No leaves it alone.
        _ = CheckPawnIoUpdateAsync();
    }

    private static async Task CheckPawnIoUpdateAsync()
    {
        try
        {
            PawnIoSetup.UpdateInfo? update = await PawnIoSetup.CheckForUpdateAsync();
            if (update == null) return;

            DebugLog.Write($"PawnIO update available: {update.Installed} -> {update.Latest}.");
            new PawnIoSetupWindow(update).ShowDialog();
        }
        catch (Exception ex)
        {
            DebugLog.Write("PawnIO update check failed.", ex);
        }
    }

    private void StartAsWatchdog(string[] args)
    {
        // Nothing will ever open a window here, so the default
        // "quit when the last window closes" would never fire.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Task.Run(() =>
        {
            try
            {
                Watchdog.RunSentinel(args);
            }
            catch (Exception ex)
            {
                DebugLog.Write("[watchdog] Fatal.", ex);
            }
            finally
            {
                Dispatcher.Invoke(() => Shutdown(0));
            }
        });
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        DebugLog.Write($"Windows session ending ({e.ReasonSessionEnding}) - releasing fans.");
        Controller.Dispose();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DebugLog.Write("Unhandled UI exception - releasing fans.", e.Exception);
        Controller.SafeRelease();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            DebugLog.Write("Unhandled exception - releasing fans.", ex);
        else
            DebugLog.Write("Unhandled non-exception throw - releasing fans.");

        Controller.SafeRelease();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Controller?.Dispose();
        DebugLog.Write("Exited cleanly.");
        base.OnExit(e);
    }
}
