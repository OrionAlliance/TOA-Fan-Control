using System.Windows;
using System.Windows.Threading;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

public partial class App : Application
{
    public static FanController Controller { get; private set; } = null!;

    // Single-instance lock: a second watchdog would save the first instance's speeds as "BIOS" and strand the fans.
    private const string InstanceMutexName = @"Global\TOA.FanControl.Instance";
    private System.Threading.Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Watchdog mode: no UI, just waits for the main app to die and releases the fans.
        if (e.Args.Length > 0 && e.Args[0] == Watchdog.Flag)
        {
            StartAsWatchdog(e.Args);
            return;
        }

        // Windows' "Installed apps" Uninstall button runs us with --uninstall.
        if (e.Args.Contains("--uninstall"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // A running instance locks the exe, so ask for a clean exit first.
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

            if (Uninstaller.Confirm(null, appIsRunning: false)) Uninstaller.Run();
            else Shutdown();
            return;
        }

        // One instance only; a second launch points at the tray instead of spawning a rival controller.
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

        // Environment banner for support: versions and bitness only, no personal data.
        DebugLog.Write(
            $"App {AppVersion.Display}  ·  " +
            $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
            $"({(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit OS, " +
            $"{(Environment.Is64BitProcess ? "64" : "32")}-bit app)  ·  " +
            $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}  ·  " +
            $"PawnIO {PawnIoSetup.GetInstalledVersion()?.ToString() ?? "not installed"}");

        // Closing a pre-window startup dialog would otherwise queue a silent app shutdown.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        FanSettings settings = SettingsStore.Load();

        // Theme before any window exists, so even the first-run dialog matches.
        ThemeManager.Apply(settings.Theme);

        // GPU power library from cache (no network), before hardware discovery logs the card's match.
        GpuLibrary.LoadCache();

        // PawnIO is required to see or drive any fan; declining the install closes the app.
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

        // Every exit path hands the fans back to the BIOS: a header stuck at a low percent can do real damage.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Controller.Dispose();
        SessionEnding += OnSessionEnding;

        // Logs sleep/resume timestamps; control re-asserts on the next tick after waking.
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

        // First run fan picker (a pump can look like a case fan), before the watchdog so it guards the chosen set.
        if (settings.SelectedFans == null && Controller.CandidateFans.Count > 0)
        {
            DebugLog.Write("No fan selection saved - showing the first-run fan picker.");
            var picker = new FanPickerWindow(Controller.CandidateFans, null, firstRun: true);
            picker.ShowDialog();

            // Closing without saving keeps every candidate.
            IReadOnlyList<(string Name, float? Rpm)> picked = Controller.CandidateFans;
            List<string> chosen = picker.Selection ?? picked.Select(f => f.Name).ToList();
            Controller.UpdateSettings(s =>
            {
                s.SelectedFans = chosen;
                NewFans.RecordDecisions(s, picked); // unchecked here counts as decided, never ask again
            }, reresolve: true);
        }

        // Ask once about new spinning headers, before the watchdog so a yes is guarded.
        AskAboutNewFans();

        // Offer start-with-Windows once; any answer ends the asking, the cog toggle changes it later.
        if (!settings.StartupOffered && !StartupTask.IsEnabled() && StartupTask.FolderIsSafe())
        {
            DebugLog.Write("Offering start-with-Windows (one-time).");
            bool wants = MessageWindow.Confirm(null, "Start with Windows?",
                "This app only guards your fans while it's running.\n\n" +
                "Want it to start with Windows, minimized to the tray? Until it " +
                "starts, your BIOS curve runs the fans. You can change this " +
                "anytime from the ⚙ menu.",
                "Yes, start with Windows", "Not now");
            if (wants) StartupTask.Enable();
            DebugLog.Write($"Start-with-Windows offer answered: {(wants ? "yes" : "no")}.");
            Controller.UpdateSettings(s => s.StartupOffered = true, reresolve: false);
        }

        // Tasks from older builds die after 3 days of uptime - fix them in place.
        _ = Task.Run(StartupTask.HealIfOutdated);

        // Delete old ~74 MB update installers.
        _ = Task.Run(AppUpdate.CleanUpOldInstallers);

        // Block until the watchdog grabs the fans first, so it holds the real BIOS settings to hand back.
        WatchdogLink? link = Watchdog.LaunchAndWait(
            Controller.ControlledFanNames, TimeSpan.FromSeconds(20));

        Controller.AttachWatchdog(link);
        Controller.BeginControl();

        var main = new MainWindow();
        MainWindow = main;

        // --minimized (start-with-Windows): the window exists for the tray icon but stays hidden.
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

        // Daily update checks, retried every 15 min while fullscreen or Game Mode would make a popup steal focus.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _updateTimer.Tick += async (_, _) => await MaybeCheckForUpdatesAsync();
        _updateTimer.Start();
        _ = MaybeCheckForUpdatesAsync(); // first check now, not in 15 minutes

        _ = MaybeShowGpuLibraryNoticeAsync(main);
    }

    /// <summary>One-time notice when this GPU is missing from the library, plus a balloon once it gets added.</summary>
    private async Task MaybeShowGpuLibraryNoticeAsync(MainWindow main)
    {
        string? gpu = Controller.GpuName;
        if (gpu == null)
        {
            DebugLog.Write("GPU library notice: no GPU detected - nothing to ask.");
            return;
        }

        // No power sensor means true load is impossible, so don't ask for watts.
        if (!Controller.GpuHasPowerSensor)
        {
            DebugLog.Write($"GPU library notice: '{gpu}' has no power sensor - true load impossible, notice skipped.");
            return;
        }

        // Decide from a fresh library: a stale "unlisted" invites a typed guess that outranks the real row.
        if (await GpuLibrary.RefreshAsync()) Controller.RefreshGpuMaxFromLibrary();

        // Wait for a popup-safe moment, then decide once and stop.
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(i == 0 ? 4 : 15));
            if (!ScreenState.PopupsSafe() || GameModeActive()) continue;
            if (!GpuLibrary.IsLoaded) continue; // offline with no cache - next session

            bool listed = GpuLibrary.MaxWattsFor(gpu) != null;
            string? shownFor = Controller.Settings.GpuNoticeShownFor;

            bool userSet = Controller.GpuUserOverrideActive;

            if (!listed && !userSet && shownFor != gpu)
            {
                DebugLog.Write($"GPU library notice: '{gpu}' unlisted - asking the user for its max watts (once).");
                var dlg = new GpuWattsWindow(main, gpu);
                dlg.ShowDialog();
                Controller.UpdateSettings(s =>
                {
                    s.GpuNoticeShownFor = gpu;
                    if (dlg.Watts is { } w)
                    {
                        s.GpuUserMaxWattsFor = gpu;
                        s.GpuUserMaxWatts = w;
                    }
                }, reresolve: false);
                if (dlg.Watts is { } watts)
                {
                    Controller.SetGpuMaxWattsOverride(watts);
                    MessageWindow.Show(main, "You're all set",
                        $"{gpu} is saved at {watts}W max.\n\n" +
                        "The cyan markers now show TRUE load - watts pulled versus your " +
                        "card's maximum - starting right now. No restart needed.");
                }
            }
            else if (listed && !userSet && shownFor == gpu)
            {
                DebugLog.Write($"GPU library notice: '{gpu}' now listed - markers show true load.");
                main.ShowTrayBalloon("TOA - Fan Control",
                    $"Good news: your {gpu} was added to the GPU library - " +
                    "the cyan markers now show its true load.");
                Controller.UpdateSettings(s => s.GpuNoticeShownFor = null, reresolve: false);
            }
            else
            {
                DebugLog.Write($"GPU library notice: '{gpu}' - nothing to show (listed={listed}, userSet={userSet}).");
            }
            return;
        }
        DebugLog.Write($"GPU library notice: no popup-safe moment in 10 minutes (library loaded: {GpuLibrary.IsLoaded}) - next launch retries.");
    }

    private DispatcherTimer? _updateTimer;
    private DateTime _nextUpdateCheck = DateTime.Now; // due immediately at launch

    private async Task MaybeCheckForUpdatesAsync()
    {
        if (DateTime.Now < _nextUpdateCheck) return;

        // No popups mid-game; the timer retries.
        if (!ScreenState.PopupsSafe() || GameModeActive())
        {
            DebugLog.Write("Update check due, but the screen is busy (fullscreen/Game Mode) - waiting.");
            return;
        }

        _nextUpdateCheck = DateTime.Now.AddHours(24);
        await CheckForUpdatesAsync();
    }

    /// <summary>Manual update check that skips the daily gate; false means tell the user they're current.</summary>
    public async Task<bool> CheckForUpdatesNowAsync()
    {
        DebugLog.Write("Manual update check (Settings).");
        _nextUpdateCheck = DateTime.Now.AddHours(24); // counts as today's check
        return await CheckForUpdatesAsync();
    }

    /// <summary>Checks for updates every time the window is opened from the tray.</summary>
    public async Task CheckOnUserReturnAsync()
    {
        _nextUpdateCheck = DateTime.Now.AddHours(24); // counts as today's check
        DebugLog.Write("Window opened - checking for updates.");
        await CheckForUpdatesAsync();
    }

    private static bool GameModeActive() =>
        Current.Windows.OfType<GameModeWindow>().Any(w => w.IsVisible);

    private static Task<bool>? _checkRun;

    // One check at a time; a second request joins the running one so popups never stack.
    private static async Task<bool> CheckForUpdatesAsync()
    {
        if (_checkRun != null)
        {
            DebugLog.Write("Update check already running - joining it.");
            return await _checkRun;
        }

        _checkRun = RunUpdateChecksAsync();
        try { return await _checkRun; }
        finally { _checkRun = null; }
    }

    private static async Task<bool> RunUpdateChecksAsync()
    {
        DebugLog.Write("Checking PawnIO and .NET for updates.");
        bool offered = false;
        bool restartWanted = false; // restart ONCE after all checks, never mid-run

        // GPU library refresh (sends nothing about this PC), guarded so a failure can't kill the check run.
        try
        {
            if (await GpuLibrary.RefreshAsync()) Controller.RefreshGpuMaxFromLibrary();
        }
        catch (Exception ex)
        {
            DebugLog.Write("GPU library refresh/re-match failed.", ex);
        }

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
                // The app installer relaunches on its own, so skip our restart.
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

    private static void AskAboutNewFans()
    {
        IReadOnlyList<(string Name, float? Rpm)> candidates = Controller.CandidateFans;

        // First run of this feature: existing setup counts as decided, so a pump left unchecked is never nagged.
        if (Controller.Settings.AskedFans == null)
        {
            Controller.UpdateSettings(s => NewFans.RecordDecisions(s, candidates), reresolve: false);
            DebugLog.Write($"New-fan check set up: {Controller.Settings.AskedFans?.Count ?? 0} unchecked spinning header(s) count as already decided.");
            return;
        }

        foreach (string name in NewFans.Find(candidates, Controller.Settings))
        {
            string shown = FanName.Display(name);
            DebugLog.Write($"New fan detected on '{name}' - asking.");
            bool drive = MessageWindow.ConfirmRisky(null, "New fan detected",
                $"A fan is spinning on {shown}, which the app isn't driving yet.\n\n" +
                "Liquid-cooled? Make sure this isn't your pump - slowing a pump can " +
                "overheat your CPU. Not sure what it is? Choose No: it simply stays on " +
                "your BIOS curve, exactly as it is now.\n\n" +
                "Want the app to drive it along with your other case fans?",
                "Yes, drive it", "No, leave it on the BIOS");

            Controller.UpdateSettings(s =>
            {
                s.AskedFans!.Add(name);
                if (drive) s.SelectedFans?.Add(name);
            }, reresolve: drive);
            DebugLog.Write($"New fan '{name}': {(drive ? "user chose to drive it" : "left on the BIOS")}.");
        }
    }

    private void StartAsWatchdog(string[] args)
    {
        // No windows here, so shutdown must be explicit.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Hand the fans back at sign-out/shutdown, capped at 3s.
        SessionEnding += (_, _) =>
        {
            if (!Watchdog.ReleaseForSessionEnd(TimeSpan.FromSeconds(3)))
                DebugLog.Write("[watchdog] Session ending - fan handback did not confirm within 3s.");
        };

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
        Controller.Dispose(); // stops the timer first so no tick re-grabs the fans
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            DebugLog.Write("Unhandled exception - releasing fans.", ex);
        else
            DebugLog.Write("Unhandled non-exception throw - releasing fans.");

        Controller.Dispose(); // stops the timer first so no tick re-grabs the fans
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Controller?.Dispose();
        DebugLog.Write("Exited cleanly.");
        base.OnExit(e);
    }
}
