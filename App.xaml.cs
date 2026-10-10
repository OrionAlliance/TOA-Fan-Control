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

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Controller?.Dispose();
        DebugLog.Write("Exited cleanly.");
        base.OnExit(e);
    }
}
