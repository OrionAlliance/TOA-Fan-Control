// One time notice when this GPU is missing from the GPU library.
using System.Windows;
using System.Windows.Threading;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

public partial class App
{
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
}
