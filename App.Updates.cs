// Daily, manual and on return update checks for PawnIO, .NET and the app.
using System.Windows;
using System.Windows.Threading;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

public partial class App
{
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
}
