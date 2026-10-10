using System.IO;
using System.Windows;

namespace FanControlSetup;

/// <summary>
/// The installer's entry point. A self-contained exe (it carries its own .NET) so
/// it runs on a brand-new PC that has no .NET at all - which is the whole reason a
/// separate installer exists: the app can't install the .NET it needs to run.
/// </summary>
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
            // A startup crash must never be silent - write it down (usernames scrubbed) and show it.
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
