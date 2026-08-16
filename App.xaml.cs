using System.Windows;
using System.Windows.Threading;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

public partial class App : Application
{
    public static FanController Controller { get; private set; } = null!;

    // Held for the app's whole life. Two instances would both drive the fans -
    // and worse, the second one's watchdog captures the FIRST instance's software
    // state as "BIOS", so whichever exits last parks the fans there until reboot.
    // Proven live in the 2026-07-21 failure-mode audit.
    private const string InstanceMutexName = @"Global\TOA.FanControl.Instance";
    private System.Threading.Mutex? _instanceMutex;

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

        // Windows' "Installed apps" Uninstall button runs us with --uninstall.
        // Same confirm-and-remove flow as Settings → Uninstall, no controller.
        if (e.Args.Contains("--uninstall"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // A running instance holds the exe lock, so the folder removal would
            // half-fail. Ask for a clean exit first - that also releases the fans.
            if (System.Threading.Mutex.TryOpenExisting(InstanceMutexName,
                    out System.Threading.Mutex? running))
            {
                running.Dispose();
                MessageWindow.Show(null, "TOA - Fan Control is currently running",
                    "Exit it first (right-click the fan icon in the system tray → " +
                    "Exit), then run Uninstall again.");
                Shutdown();
                return;
            }

            bool yes = MessageWindow.Confirm(null,
                "Uninstall TOA - Fan Control?",
                "This will remove TOA - Fan Control from this PC - the app, its " +
                "settings, its log, and its shortcuts.\n\n(PawnIO and .NET stay: " +
                "they're shared system components other software can use.)",
                "Uninstall", "Cancel");

            if (yes) Uninstaller.Run();
            else Shutdown();
            return;
        }

        // One instance only. The tray icon is easy to miss - a second double-click
        // should point at it, not spawn a rival controller.
        _instanceMutex = new System.Threading.Mutex(
            true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            DebugLog.Write("Second instance blocked - the app is already running.");
            MessageWindow.Show(null, "Already running",
                "TOA - Fan Control is already running - check the system tray " +
                "(double-click the fan icon to open it).");
            Shutdown();
            return;
        }

        DebugLog.Write(new string('=', 60));
        DebugLog.Write("TOA - Fan Control starting.");

        // Environment banner - the first thing support needs from any "doesn't
        // work" report. No personal data: versions and bitness only.
        Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                    ?? new Version(0, 0, 0);
        DebugLog.Write(
            $"App v{v.Major}.{v.Minor}.{v.Build}  ·  " +
            $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
            $"({(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit OS, " +
            $"{(Environment.Is64BitProcess ? "64" : "32")}-bit app)  ·  " +
            $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}  ·  " +
            $"PawnIO {PawnIoSetup.GetInstalledVersion()?.ToString() ?? "not installed"}");

        // Startup shows dialogs BEFORE the main window exists (first-run PawnIO,
        // the fan picker). Under the default OnLastWindowClose rule, closing one
        // of those queues an app shutdown that later executes even though the
        // main window opened - the app silently died ~15s after first run.
        // So: nothing shuts us down implicitly until the real window is up.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        FanSettings settings = SettingsStore.Load();

        // Theme before any window exists, so even the first-run dialog matches.
        ThemeManager.Apply(settings.Theme);

        // GPU power library from cache (no network) - loaded before the hardware
        // opens so discovery can log this card's match alongside its sensors.
        GpuLibrary.LoadCache();

        // PawnIO is REQUIRED - without it the app can't see or drive a single fan.
        // So it's mandatory, not optional: if it's missing and the user declines to
        // install it, there's nothing for the app to do, and it closes.
        if (!PawnIoSetup.IsInstalled())
        {
            DebugLog.Write("PawnIO not installed - showing first-run setup.");
            var setup = new PawnIoSetupWindow();
            setup.ShowDialog();

            if (!setup.Installed)
            {
                DebugLog.Write("PawnIO declined - the app can't run without it. Closing.");
                Shutdown();
                return;
            }

            if (setup.RebootRequired)
            {
                MessageWindow.Show(null, "One reboot to go",
                    "PawnIO is installed, but Windows needs a reboot to finish " +
                    "loading it.\n\nReboot, then open TOA - Fan Control again.");
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

        // Sleep/resume visibility. The controller re-writes the fans every tick,
        // so control re-asserts within a second of waking - these lines are the
        // timestamps that prove it when a report says "weird after sleep".
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        try
        {
            Controller.OpenHardware();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Controller failed to start.", ex);
            MessageWindow.Show(null, "Couldn't open the fan hardware",
                "This app needs to run as administrator to reach the motherboard's " +
                "fan chip.\n\n" + ex.Message);
            Shutdown(1);
            return;
        }

        // First run: let the person confirm which case fans the app may drive.
        // Exists for boards that name every header "Fan #N", where a liquid-cooler
        // pump is indistinguishable from a case fan - only the builder knows.
        // Must happen BEFORE the watchdog, so it guards exactly the chosen set.
        if (settings.SelectedFans == null && Controller.CandidateFans.Count > 0)
        {
            DebugLog.Write("No fan selection saved - showing the first-run fan picker.");
            var picker = new FanPickerWindow(Controller.CandidateFans, null, firstRun: true);
            picker.ShowDialog();

            // Closing the window without saving counts as "keep them all" - the
            // default is every candidate, same as before the picker existed.
            List<string> chosen = picker.Selection
                ?? Controller.CandidateFans.Select(f => f.Name).ToList();
            Controller.UpdateSettings(s => s.SelectedFans = chosen);
        }

        // The watchdog must take the fans BEFORE we write to any of them: whoever
        // grabs a header first is the only one holding its real BIOS settings, and
        // therefore the only one that can ever hand it back. Blocking here is the
        // whole point - if we got in first, a force-kill would strand the fans.
        WatchdogLink? link = Watchdog.LaunchAndWait(
            Controller.ControlledFanNames, TimeSpan.FromSeconds(20));

        Controller.AttachWatchdog(link);
        Controller.BeginControl();

        var main = new MainWindow();
        MainWindow = main;

        // --minimized: the start-with-Windows task uses this so a boot brings the
        // app up silently in the tray. The window exists (it owns the tray icon
        // and all rendering) - it just isn't shown until the tray is clicked.
        if (e.Args.Contains("--minimized"))
        {
            DebugLog.Write("Started minimized to the tray (--minimized).");
            WorkingSet.Trim();
        }
        else
        {
            main.Show();
        }

        // From here the real window governs the app's life: closing it exits.
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Quietly check both prerequisites for newer versions - at launch and then
        // every 24 hours, since this app can sit in the tray for weeks. Never blocks
        // startup; offline just no-ops. Each found update gets its own popup - Yes
        // installs it, No leaves it alone. .NET is checked by the app itself because
        // Windows Update only services .NET when "Receive updates for other
        // Microsoft products" is on - and nobody's PC can be trusted to have it on.
        //
        // The 15-minute timer is the retry heartbeat: when a check comes due while
        // something fullscreen is up (game, video, presentation) or Game Mode is on,
        // the popup would steal focus - so it waits and retries until the screen is
        // clear, then checks.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _updateTimer.Tick += async (_, _) => await MaybeCheckForUpdatesAsync();
        _updateTimer.Start();
        _ = MaybeCheckForUpdatesAsync(); // first check now, not in 15 minutes
    }

    private DispatcherTimer? _updateTimer;
    private DateTime _nextUpdateCheck = DateTime.Now; // due immediately at launch

    private async Task MaybeCheckForUpdatesAsync()
    {
        if (DateTime.Now < _nextUpdateCheck) return;

        // Mid-game is the wrong moment for a popup. Hold off; the timer retries.
        if (!ScreenState.PopupsSafe() || GameModeActive())
        {
            DebugLog.Write("Update check due, but the screen is busy (fullscreen/Game Mode) - waiting.");
            return;
        }

        _nextUpdateCheck = DateTime.Now.AddHours(24);
        await CheckForUpdatesAsync();
    }

    /// <summary>
    /// Settings → Check for updates: run all three checks right now, skipping the
    /// daily gate (the user asked, so popups are welcome). Returns true if any
    /// update was offered - false means "tell them they're current", because a
    /// manual check that ends in silence reads as broken.
    /// </summary>
    public async Task<bool> CheckForUpdatesNowAsync()
    {
        DebugLog.Write("Manual update check (Settings).");
        _nextUpdateCheck = DateTime.Now.AddHours(24); // counts as today's check
        return await CheckForUpdatesAsync();
    }

    /// <summary>
    /// Opening the window from the tray is the user coming to look - the right
    /// moment to surface an update they missed. Rate-limited to once an hour so
    /// bouncing the window can't hammer the update APIs.
    /// </summary>
    public async Task CheckOnUserReturnAsync()
    {
        if (DateTime.Now - _lastCheckRan < TimeSpan.FromHours(1)) return;
        _nextUpdateCheck = DateTime.Now.AddHours(24); // counts as today's check
        DebugLog.Write("Window opened - checking for updates.");
        await CheckForUpdatesAsync();
    }

    private static DateTime _lastCheckRan = DateTime.MinValue;

    private static bool GameModeActive() =>
        Current.Windows.OfType<GameModeWindow>().Any(w => w.IsVisible);

    private static async Task<bool> CheckForUpdatesAsync()
    {
        _lastCheckRan = DateTime.Now;
        DebugLog.Write("Checking PawnIO and .NET for updates.");
        bool offered = false;
        bool restartWanted = false; // restart ONCE after all checks, never mid-run

        // The GPU power library rides the same daily cadence - a whole-file
        // fetch that carries nothing about this PC, silent when offline.
        await GpuLibrary.RefreshAsync();

        try
        {
            PawnIoSetup.UpdateInfo? pawnIo = await PawnIoSetup.CheckForUpdateAsync();
            if (pawnIo != null)
            {
                DebugLog.Write($"PawnIO update available: {pawnIo.Installed} -> {pawnIo.Latest}.");
                offered = true;
                var dlg = new PawnIoSetupWindow(pawnIo);
                dlg.ShowDialog();
                restartWanted |= dlg.RestartWanted;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("PawnIO update check failed.", ex);
        }

        try
        {
            DotNetUpdate.UpdateInfo? dotnet = await DotNetUpdate.CheckForUpdateAsync();
            if (dotnet != null)
            {
                DebugLog.Write($".NET update available: {dotnet.Installed} -> {dotnet.Latest}.");
                offered = true;
                var dlg = new PawnIoSetupWindow(dotnet);
                dlg.ShowDialog();
                restartWanted |= dlg.RestartWanted;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write(".NET update check failed.", ex);
        }

        try
        {
            AppUpdate.UpdateInfo? app = await AppUpdate.CheckForUpdateAsync();
            if (app != null)
            {
                DebugLog.Write($"App update available: v{app.Installed} -> v{app.Latest}.");
                offered = true;
                var dlg = new PawnIoSetupWindow(app);
                dlg.ShowDialog();
                // The app updater's installer owns close-and-relaunch itself -
                // don't stack a second restart on top of it.
                if (dlg.Installed) restartWanted = false;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("App update check failed.", ex);
        }

        if (restartWanted)
        {
            DebugLog.Write("Restarting the app so the installed updates take effect now.");
            AppRestart.AfterExit();
            Current.Shutdown();
        }

        return offered;
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

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
            DebugLog.Write("System going to sleep.");
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            DebugLog.Write("System resumed from sleep - fan control re-asserts on the next tick.");
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
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Controller?.Dispose();
        DebugLog.Write("Exited cleanly.");
        base.OnExit(e);
    }
}
