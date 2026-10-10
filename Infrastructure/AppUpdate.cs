using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using FanControlApp.Cooling; // InstallResult record

namespace FanControlApp.Infrastructure;

/// <summary>Self-updater from GitHub releases; releases are unsigned, so trust is TLS plus repo ownership.</summary>
public static class AppUpdate
{
    private const string LatestApi =
        "https://api.github.com/repos/OrionAlliance/TOA-Fan-Control/releases/latest";

    public sealed record UpdateInfo(Version Installed, Version Latest, string DownloadUrl);

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(5);

    private static readonly string UpdatesDir = Path.Combine(AppPaths.ExeDir, "Updates");
    private static readonly string UnpackDir = Path.Combine(UpdatesDir, "Unpacked");

    /// <summary>Deletes leftover update installers (about 74 MB each).</summary>
    public static void CleanUpOldInstallers()
    {
        try
        {
            // Checks both Updates and the old Temp location.
            IEnumerable<string> old = Directory.Exists(UpdatesDir)
                ? Directory.EnumerateFiles(UpdatesDir, "TOA-FanControl-Setup-*.exe")
                : Enumerable.Empty<string>();
            old = old.Concat(Directory.EnumerateFiles(Path.GetTempPath(), "TOA-FanControl-Setup-*.exe"));

            int removed = 0, inUse = 0;
            long bytes = 0;
            foreach (string f in old.ToList())
            {
                try
                {
                    long size = new FileInfo(f).Length;
                    File.Delete(f);
                    removed++;
                    bytes += size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    inUse++; // the installer that just relaunched us is still finishing up
                }
            }
            if (removed + inUse > 0)
                DebugLog.Write($"Old update installers: removed {removed} ({bytes / (1024 * 1024)} MB), " +
                               $"{inUse} still in use - removed next start.");

            // Installer unpack folders, in Temp\.net or Updates\Unpacked.
            string dotnetTemp = Path.Combine(Path.GetTempPath(), ".net");
            IEnumerable<string> unpacked = Directory.Exists(dotnetTemp)
                ? Directory.EnumerateDirectories(dotnetTemp, "TOA-FanControl-Setup-*")
                : Enumerable.Empty<string>();
            if (Directory.Exists(UnpackDir)) unpacked = unpacked.Append(UnpackDir);
            int gone = 0;
            foreach (string d in unpacked.ToList())
            {
                try { Directory.Delete(d, recursive: true); gone++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still in use - next start */ }
            }
            if (gone > 0) DebugLog.Write($"Old installer unpack folders removed: {gone}.");
        }
        catch (Exception ex)
        {
            DebugLog.Write("Old update installer cleanup failed.", ex);
        }
    }

    /// <summary>Newer release than this build? Null = current, or offline (no nagging).</summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            Version installed = AppVersion.Current;

            using HttpClient http = Downloads.NewClient(HttpTimeout);
            using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi));

            string? tag = doc.RootElement.GetProperty("tag_name").GetString();
            if (tag == null) return null;
            var latest = Version.Parse(tag.TrimStart('v', 'V'));

            // First .exe asset is the installer; use browser_download_url, never a hand-built URL.
            string? url = null;
            foreach (JsonElement a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString()?.EndsWith(".exe") ?? false)
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (url == null) return null; // no installer in this release

            return latest > installed ? new UpdateInfo(installed, latest, url) : null;
        }
        catch (Exception ex)
        {
            DebugLog.Write("App update check failed (offline?).", ex);
            return null;
        }
    }

    /// <summary>Downloads and starts the installer, then exits so it can replace the exe (fans go back to BIOS).</summary>
    public static Func<IProgress<string>, Task<PawnIoSetup.InstallResult>> InstallerFor(UpdateInfo u)
        => async progress =>
    {
        try
        {
            // Admin-only drop-off, so nothing can swap the installer between download and run.
            Directory.CreateDirectory(UpdatesDir);
            if (!FolderLock.LockToAdmins(UpdatesDir))
                throw new InvalidOperationException("couldn't secure the download folder");
            string temp = Path.Combine(UpdatesDir, $"TOA-FanControl-Setup-{u.Latest}.exe");

            progress.Report($"Downloading v{u.Latest} from GitHub…");
            await Downloads.DownloadToFileAsync(u.DownloadUrl, temp, HttpTimeout);

            // Filename only, paths never go in the log.
            DebugLog.Write($"App update downloaded: {Path.GetFileName(temp)}");
            progress.Report("Starting the installer - the app will close…");

            // --update = silent replace in place, then relaunch.
            var psi = new ProcessStartInfo
            {
                FileName = temp,
                Arguments = $"--update \"{AppPaths.ExeDir.TrimEnd('\\')}\"",
                UseShellExecute = false,
            };
            // Keep the installer's unpack inside the locked folder, not Temp.
            psi.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = UnpackDir;
            Process.Start(psi);

            // Let the dialog show the message, then exit.
            _ = Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(1500);
                DebugLog.Write("Exiting for app update - installer takes over.");
                Application.Current.Shutdown();
            });

            return new PawnIoSetup.InstallResult(true, false,
                "Update started - this app will close and the installer takes over.");
        }
        catch (Exception ex)
        {
            DebugLog.Write("App update failed.", ex);
            return new PawnIoSetup.InstallResult(false, false,
                "Couldn't download the update. You can grab it manually from the " +
                "GitHub Releases page. " + ex.Message);
        }
    };
}
