using System.IO;
using System.Windows;

namespace FanControlSetup;

/// <summary>Installer entry point; self-contained so it runs on a PC with no .NET and can install it.</summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnLastWindowClose };
            app.Run(new SetupWindow());
        }
        catch (Exception ex)
        {
            // Never fail silently: log the crash (usernames scrubbed) and show it.
            string details = FanControlApp.Infrastructure.DebugLog.Scrub(ex.ToString());
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "setup_crash.log"), details);
            }
            catch { /* nothing more we can do */ }

            MessageBox.Show(details, "TOA - Fan Control Setup - startup error",
                MessageBoxButton.OK, MessageBoxImage.None);
        }
    }
}
