using System.Diagnostics;
using System.IO;
using System.Reflection;
using FanControlApp.Infrastructure;
using Microsoft.Win32;

namespace FanControlSetup;

/// <summary>Writes the embedded framework-dependent app exe, adds shortcuts and registration, and launches it.</summary>
public static class AppInstaller
{
    public const string AppName = "TOA - Fan Control";
    private const string ExeName = "TOA - Fan Control.exe";

    /// <summary>Suggested visible folder (not AppData) that holds the whole app: exe, settings and log.</summary>
    public const string DefaultInstallDir = @"C:\TOA - Fan Control";

    /// <summary>Install folder, set from the location prompt before extracting.</summary>
    public static string InstallDir { get; set; } = DefaultInstallDir;

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    /// <summary>Writes the app exe, retrying while a self-updating old app and its watchdog still lock it.</summary>
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
                System.Threading.Thread.Sleep(500); // old exe still locked
                src.Position = 0;
            }
        }

        DebugLog.Write("App written.");
        LockFolder();
    }

    // Everything the app keeps in its folder; anything else means a shared folder.
    private static readonly string[] OwnEntries =
        { ExeName, "Settings", "Updates", "fan_debug.log", "fan_debug.log.old" };

    // The logon task runs this exe as admin, so nothing without admin may change its folder.
    private static void LockFolder()
    {
        bool dedicated = Directory.EnumerateFileSystemEntries(InstallDir)
            .All(p => OwnEntries.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase));
        if (!dedicated)
        {
            DebugLog.Write("App folder holds other files too - left unlocked so nothing else is affected.");
            return;
        }

        DebugLog.Write(FolderLock.LockToAdmins(InstallDir)
            ? "App folder locked: only administrators can change it."
            : "App folder lock FAILED - it stays as it was.");
    }

    /// <summary>Start-menu shortcut, always created; the app manifest triggers UAC itself.</summary>
    public static void CreateStartMenuShortcut() => WriteShortcut(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"), "Start menu");

    /// <summary>Optional desktop shortcut; the installer asks first.</summary>
    public static void CreateDesktopShortcut() => WriteShortcut(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), AppName + ".lnk"), "Desktop");

    private static void WriteShortcut(string lnkPath, string where)
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
            DebugLog.Write($"{where} shortcut created.");
        }
        catch (Exception ex)
        {
            // Non-fatal: the exe is installed and launchable.
            DebugLog.Write("Shortcut creation failed (non-fatal).", ex);
        }
    }

    /// <summary>Registers in Installed apps; uninstall runs the app with --uninstall, which removes this key.</summary>
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
            // Cosmetic, so a failure must not fail the install.
            DebugLog.Write("Installed-apps registration failed (non-fatal).", ex);
        }
    }

    /// <summary>Launches the installed app, inheriting Setup's admin without a second prompt.</summary>
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
