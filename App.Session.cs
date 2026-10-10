// Session end, sleep and resume, and unhandled exception handlers.
using System.Windows;
using System.Windows.Threading;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

public partial class App
{
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
}
