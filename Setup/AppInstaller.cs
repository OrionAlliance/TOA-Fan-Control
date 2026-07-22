using System.Diagnostics;
using System.IO;
using System.Reflection;
using FanControlApp.Infrastructure;
using Microsoft.Win32;

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

    /// <summary>
    /// A visible, obvious folder - not AppData, where nobody can find anything.
    /// The user picks the actual location at install time; this is the suggestion.
    /// Settings and the debug log live next to the exe, so this one folder IS the
    /// whole app - same portable convention as every other TOA app.
    /// </summary>
    public const string DefaultInstallDir = @"C:\TOA - Fan Control";

    /// <summary>Where the app goes - set from the location popup before extracting.</summary>
    public static string InstallDir { get; set; } = DefaultInstallDir;

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    /// <summary>
    /// Write the embedded app exe into the install folder. Retries for a while:
    /// during a self-update the OLD app is still exiting when we start, and its
    /// exe stays locked until it (and its watchdog) are gone.
    /// </summary>
    public static void ExtractApp()
    {
        Directory.CreateDirectory(InstallDir);

        using Stream? src = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.exe")
            ?? throw new InvalidOperationException("The app payload is missing from this installer.");

        DateTime deadline = DateTime.Now.AddSeconds(20);
        while (true)
        {
            try
            {
                using FileStream dst = File.Create(InstalledExe);
                src.CopyTo(dst);
                break;
            }
            catch (IOException) when (DateTime.Now < deadline)
            {
                System.Threading.Thread.Sleep(500); // old exe still locked - wait it out
                src.Position = 0;
            }
        }

        DebugLog.Write($"App written to {InstalledExe}.");
    }

    /// <summary>
    /// Start-menu shortcut - always created, never asked about. An installed app
    /// that isn't in the Start menu reads as shady, not minimal. The app's manifest
    /// asks for admin, so the shortcut triggers UAC on its own - no flag needed.
    /// </summary>
    public static void CreateStartMenuShortcut() => WriteShortcut(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"));

    /// <summary>Desktop shortcut - the optional one; the installer asks first.</summary>
    public static void CreateDesktopShortcut() => WriteShortcut(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), AppName + ".lnk"));

    private static void WriteShortcut(string lnkPath)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(
                Type.GetTypeFromProgID("WScript.Shell")!)!;
            var link = shell.CreateShortcut(lnkPath);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallDir;
            link.IconLocation = InstalledExe + ",0";
            link.Description = "Temperature-driven case-fan control";
            link.Save();
            DebugLog.Write($"Shortcut created: {lnkPath}");
        }
        catch (Exception ex)
        {
            // A missing shortcut isn't fatal - the exe is installed and launchable.
            DebugLog.Write("Shortcut creation failed (non-fatal).", ex);
        }
    }

    /// <summary>
    /// Register in Windows' "Installed apps" list, like a proper install. The
    /// uninstall command runs the app with --uninstall, which shows the same
    /// confirm-and-remove flow as Settings → Uninstall. The app's uninstaller
    /// deletes this key again.
    /// </summary>
    public static void RegisterInInstalledApps()
    {
        try
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName);

            string version = FileVersionInfo.GetVersionInfo(InstalledExe).FileVersion ?? "1.0.0";
            long sizeKb = new FileInfo(InstalledExe).Length / 1024;

            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "TOA");
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("DisplayIcon", InstalledExe);
            key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);

            DebugLog.Write($"Registered in Installed apps (v{version}).");
        }
        catch (Exception ex)
        {
            // Registration is cosmetic - a failure must not fail the install.
            DebugLog.Write("Installed-apps registration failed (non-fatal).", ex);
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
