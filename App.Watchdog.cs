// Starts this process in watchdog mode.
using System.Windows;
using System.Windows.Threading;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

public partial class App
{
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
}
