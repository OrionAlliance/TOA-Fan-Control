using System.Diagnostics;

namespace FanControlApp.Infrastructure;

/// <summary>Opens a web link in the default browser.</summary>
public static class Browser
{
    public static void Open(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
