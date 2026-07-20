using System.Diagnostics;
using System.IO;
using System.Reflection;
using FanControlApp.Infrastructure;

namespace FanControlSetup;

/// <summary>
/// Puts the app on the machine: writes the embedded exe into a per-user program
/// folder, drops a Start-menu shortcut, and launches it. The app is small and
/// framework-dependent - it uses the .NET we just made sure is present, so Windows
/// keeps that runtime patched from here on.
/// </summary>
public static class AppInstaller
{
    public const string AppName = "TOA - Fan Control";
    private const string ExeName = "TOA - Fan Control.exe";

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", AppName);

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    /// <summary>Write the embedded app exe into the install folder.</summary>
    public static void ExtractApp()
    {
        Directory.CreateDirectory(InstallDir);

        using Stream? src = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.exe")
            ?? throw new InvalidOperationException("The app payload is missing from this installer.");
        using FileStream dst = File.Create(InstalledExe);
        src.CopyTo(dst);

        DebugLog.Write($"App written to {InstalledExe}.");
    }

    /// <summary>Start-menu shortcut. The app's manifest asks for admin, so launching
    /// it from the shortcut triggers UAC on its own - no special flag needed here.</summary>
    public static void CreateShortcut()
    {
        string startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

        try
        {
            dynamic shell = Activator.CreateInstance(
                Type.GetTypeFromProgID("WScript.Shell")!)!;
            var link = shell.CreateShortcut(startMenu);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallDir;
            link.IconLocation = InstalledExe + ",0";
            link.Description = "Temperature-driven case-fan control";
            link.Save();
            DebugLog.Write($"Shortcut created: {startMenu}");
        }
        catch (Exception ex)
        {
            // A missing shortcut isn't fatal - the exe is installed and launchable.
            DebugLog.Write("Shortcut creation failed (non-fatal).", ex);
        }
    }

    /// <summary>Launch the freshly installed app. Setup is elevated, so it inherits
    /// admin without a second prompt.</summary>
    public static void Launch()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = InstalledExe,
            WorkingDirectory = InstallDir,
            UseShellExecute = false,
        });
        DebugLog.Write("Launched the installed app.");
    }
}
