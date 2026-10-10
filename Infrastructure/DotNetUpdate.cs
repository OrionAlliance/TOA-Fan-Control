using System.IO;
using System.Net.Http;
using System.Text.Json;
using FanControlApp.Cooling; // PawnIoSetup: signature check + InstallResult record

namespace FanControlApp.Infrastructure;

/// <summary>Checks and installs the .NET 10 Desktop Runtime itself, since Windows Update often skips it.</summary>
public static class DotNetUpdate
{
    private const string ReleasesIndex =
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";

    // The app targets net10.0-windows, so it needs WindowsDesktop 10.x.
    private const string DesktopFramework = "Microsoft.WindowsDesktop.App";
    private const int RequiredMajor = 10;

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(5);

    public sealed record UpdateInfo(Version Installed, Version Latest);

    /// <summary>Is any 10.x desktop runtime present?</summary>
    public static bool IsInstalled() => InstalledVersion() != null;

    /// <summary>The newest 10.x WindowsDesktop runtime on this machine, or null.</summary>
    public static Version? InstalledVersion()
    {
        Version? best = null;

        foreach (string root in DotnetRoots())
        {
            string dir = Path.Combine(root, "shared", DesktopFramework);
            if (!Directory.Exists(dir)) continue;

            foreach (string sub in Directory.GetDirectories(dir))
            {
                if (Version.TryParse(Path.GetFileName(sub), out Version? v) &&
                    v.Major >= RequiredMajor && (best == null || v > best))
                    best = v;
            }
        }

        return best;
    }

    private static IEnumerable<string> DotnetRoots()
    {
        // Default machine-wide install, plus whatever DOTNET_ROOT points at.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");

        string? envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot)) yield return envRoot;
    }

    /// <summary>Newer 10.x runtime available? Null when current or offline.</summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            Version? installed = InstalledVersion();
            if (installed == null) return null; // Setup installs it, not an update

            using HttpClient http = Downloads.NewClient(HttpTimeout);
            string latestStr = await GetChannelFieldAsync(http, "latest-release");
            var latest = Version.Parse(latestStr);

            return latest > installed ? new UpdateInfo(installed, latest) : null;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Checking the latest .NET version failed (offline?).", ex);
            return null;
        }
    }

    /// <summary>Downloads the runtime installer, verifies the Microsoft signature, and runs it quietly.</summary>
    public static async Task<PawnIoSetup.InstallResult> InstallAsync(IProgress<string> progress)
    {
        string temp = "";
        try
        {
            temp = Path.Combine(FolderLock.DownloadFolder(), $"windowsdesktop-runtime-{Environment.ProcessId}.exe");
            progress.Report("Finding the latest .NET 10…");
            string url = await GetInstallerUrlAsync();
            DebugLog.Write($".NET runtime installer URL: {url}");

            progress.Report("Downloading the .NET 10 runtime…");
            await Downloads.DownloadToFileAsync(url, temp, HttpTimeout);

            progress.Report("Checking the download…");
            if (!PawnIoSetup.IsTrustedAndSignedBy(temp, "Microsoft"))
            {
                DebugLog.Write(".NET installer failed signature verification - NOT running it.");
                return new PawnIoSetup.InstallResult(false, false,
                    "The .NET download didn't pass Microsoft's signature check, so it wasn't run.");
            }

            progress.Report("Installing .NET 10…");
            int exit = await Downloads.RunAndWaitAsync(temp, "/install /quiet /norestart");

            return exit switch
            {
                0 => new PawnIoSetup.InstallResult(true, false, ".NET 10 updated."),
                PawnIoSetup.RebootRequiredExitCode => new PawnIoSetup.InstallResult(true, true,
                    ".NET 10 updated - a reboot will finish it."),
                _ => new PawnIoSetup.InstallResult(false, false,
                    $"The .NET installer exited with code {exit}."),
            };
        }
        catch (Exception ex)
        {
            DebugLog.Write(".NET runtime install failed.", ex);
            return new PawnIoSetup.InstallResult(false, false,
                "Couldn't install .NET 10 automatically. " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp cleanup */ }
        }
    }

    // ---- Microsoft release metadata ------------------------------------------

    // One field of the 10.0 channel entry in Microsoft's releases index.
    private static async Task<string> GetChannelFieldAsync(HttpClient http, string field)
    {
        using JsonDocument index = JsonDocument.Parse(await http.GetStringAsync(ReleasesIndex));
        return index.RootElement.GetProperty("releases-index").EnumerateArray()
            .First(c => c.GetProperty("channel-version").GetString() == "10.0")
            .GetProperty(field).GetString()!;
    }

    /// <summary>The current 10.0 WindowsDesktop x64 .exe installer URL, from Microsoft.</summary>
    private static async Task<string> GetInstallerUrlAsync()
    {
        using HttpClient http = Downloads.NewClient(HttpTimeout);

        string channel = await GetChannelFieldAsync(http, "releases.json");

        using JsonDocument rel = JsonDocument.Parse(await http.GetStringAsync(channel));
        string latest = rel.RootElement.GetProperty("latest-release").GetString()!;

        JsonElement release = rel.RootElement.GetProperty("releases").EnumerateArray()
            .First(r => r.GetProperty("release-version").GetString() == latest);

        foreach (JsonElement f in release.GetProperty("windowsdesktop").GetProperty("files").EnumerateArray())
        {
            if (f.GetProperty("rid").GetString() == "win-x64" &&
                (f.GetProperty("name").GetString()?.EndsWith(".exe") ?? false))
                return f.GetProperty("url").GetString()!;
        }

        throw new InvalidOperationException("No win-x64 desktop-runtime installer in the release metadata.");
    }
}
