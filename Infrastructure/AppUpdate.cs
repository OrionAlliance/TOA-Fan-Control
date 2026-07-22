using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using FanControlApp.Cooling; // InstallResult record

namespace FanControlApp.Infrastructure;

/// <summary>
/// The app's own updater: compares this build against the latest GitHub release
/// and, on the user's OK, downloads that release's installer, starts it, and
/// closes the app so the installer can replace the locked exe. Settings and the
/// fan selection live in the app folder and survive the swap. Trust anchor is
/// TLS to github.com + ownership of the repo - our releases are unsigned, so
/// there is no Authenticode check to make (unlike the PawnIO/.NET downloads,
/// which are signed by their vendors and verified).
/// </summary>
public static class AppUpdate
{
    private const string LatestApi =
        "https://api.github.com/repos/OrionAlliance/TOA-Fan-Control/releases/latest";

    public sealed record UpdateInfo(Version Installed, Version Latest, string DownloadUrl);

    /// <summary>Newer release than this build? Null = current, or offline (no nagging).</summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            Version asm = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                          ?? new Version(0, 0, 0);
            var installed = new Version(asm.Major, asm.Minor, asm.Build < 0 ? 0 : asm.Build);

            using var http = NewClient();
            using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi));

            string? tag = doc.RootElement.GetProperty("tag_name").GetString();
            if (tag == null) return null;
            var latest = Version.Parse(tag.TrimStart('v', 'V'));

            // First .exe asset is the installer (GitHub dots the spaces in the name;
            // browser_download_url is always the truth - never build the URL by hand).
            string? url = null;
            foreach (JsonElement a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString()?.EndsWith(".exe") ?? false)
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (url == null) return null; // release without an installer - nothing to offer

            return latest > installed ? new UpdateInfo(installed, latest, url) : null;
        }
        catch (Exception ex)
        {
            DebugLog.Write("App update check failed (offline?).", ex);
            return null;
        }
    }

    /// <summary>
    /// The install action for the update window: download the release installer,
    /// hand it our install folder, start it, and close this app so the exe is
    /// free to replace. The watchdog returns the fans to the BIOS on our way out;
    /// the freshly installed version takes them again when it launches.
    /// </summary>
    public static Func<IProgress<string>, Task<PawnIoSetup.InstallResult>> InstallerFor(UpdateInfo u)
        => async progress =>
    {
        try
        {
            string temp = Path.Combine(Path.GetTempPath(),
                $"TOA-FanControl-Setup-{u.Latest}.exe");

            progress.Report($"Downloading v{u.Latest} from GitHub…");
            using (var http = NewClient())
            using (HttpResponseMessage resp = await http.GetAsync(
                       u.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using Stream src = await resp.Content.ReadAsStreamAsync();
                await using FileStream file = File.Create(temp);
                await src.CopyToAsync(file);
            }

            DebugLog.Write($"App update downloaded: {temp}");
            progress.Report("Starting the installer - the app will close…");

            // --update = silent replace-in-place: no location or shortcut questions,
            // just swap the app where it already lives and relaunch it.
            Process.Start(new ProcessStartInfo
            {
                FileName = temp,
                Arguments = $"--update \"{AppPaths.ExeDir.TrimEnd('\\')}\"",
                UseShellExecute = false,
            });

            // Give the dialog a beat to show the message, then get out of the way.
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

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TOA-FanControl");
        return http;
    }
}
