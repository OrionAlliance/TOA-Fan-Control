using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using FanControlApp.Cooling; // PawnIoSetup: signature check + InstallResult record

namespace FanControlApp.Infrastructure;

/// <summary>
/// Detects, updates, and (for Setup) installs the .NET 10 Desktop Runtime the app
/// runs on. Windows Update *can* keep .NET patched, but only when "Receive updates
/// for other Microsoft products" is on - and it was off on our own main PC, so no
/// stranger's machine can be trusted to have it right. The app therefore checks
/// for itself on startup, PawnIO-style: compare what's installed against
/// Microsoft's release metadata, and offer the update (Yes installs, No skips).
/// Everything downloaded is verified Microsoft-signed before it runs.
/// </summary>
public static class DotNetUpdate
{
    private const string ReleasesIndex =
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";

    // The app targets net10.0-windows: it needs the WindowsDesktop shared
    // framework, major version 10.
    private const string DesktopFramework = "Microsoft.WindowsDesktop.App";
    private const int RequiredMajor = 10;

    public sealed record UpdateInfo(Version Installed, Version Latest);

    /// <summary>Is any 10.x desktop runtime present? (Setup's install-or-not check.)</summary>
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

    /// <summary>
    /// Is a newer 10.x runtime out than the one installed? Null when there's nothing
    /// to do - already current, or offline (not a reason to nag).
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            Version? installed = InstalledVersion();
            if (installed == null) return null; // Setup's problem, not an "update"

            using var http = NewClient();
            string latestStr = await GetLatestReleaseAsync(http);
            var latest = Version.Parse(latestStr);

            return latest > installed ? new UpdateInfo(installed, latest) : null;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Checking the latest .NET version failed (offline?).", ex);
            return null;
        }
    }

    /// <summary>
    /// Download the current runtime installer from Microsoft, verify it's
    /// Microsoft-signed, and run it quietly. Same result shape as the PawnIO
    /// installer so the one update window can drive either.
    /// </summary>
    public static async Task<PawnIoSetup.InstallResult> InstallAsync(IProgress<string> progress)
    {
        string temp = Path.Combine(Path.GetTempPath(),
            $"windowsdesktop-runtime-{Environment.ProcessId}.exe");

        try
        {
            progress.Report("Finding the latest .NET 10…");
            string url = await GetInstallerUrlAsync();
            DebugLog.Write($".NET runtime installer URL: {url}");

            progress.Report("Downloading the .NET 10 runtime…");
            await DownloadAsync(url, temp);

            progress.Report("Checking the download…");
            if (!PawnIoSetup.IsTrustedAndSignedBy(temp, "Microsoft"))
            {
                DebugLog.Write(".NET installer failed signature verification - NOT running it.");
                return new PawnIoSetup.InstallResult(false, false,
                    "The .NET download didn't pass Microsoft's signature check, so it wasn't run.");
            }

            progress.Report("Installing .NET 10…");
            int exit = await RunAsync(temp, "/install /quiet /norestart");

            return exit switch
            {
                0 => new PawnIoSetup.InstallResult(true, false, ".NET 10 updated."),
                3010 => new PawnIoSetup.InstallResult(true, true,
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

    private static async Task<string> GetLatestReleaseAsync(HttpClient http)
    {
        using JsonDocument index = JsonDocument.Parse(await http.GetStringAsync(ReleasesIndex));
        return index.RootElement.GetProperty("releases-index").EnumerateArray()
            .First(c => c.GetProperty("channel-version").GetString() == "10.0")
            .GetProperty("latest-release").GetString()!;
    }

    /// <summary>The current 10.0 WindowsDesktop x64 .exe installer URL, from Microsoft.</summary>
    private static async Task<string> GetInstallerUrlAsync()
    {
        using var http = NewClient();

        using JsonDocument index = JsonDocument.Parse(await http.GetStringAsync(ReleasesIndex));
        string channel = index.RootElement.GetProperty("releases-index").EnumerateArray()
            .First(c => c.GetProperty("channel-version").GetString() == "10.0")
            .GetProperty("releases.json").GetString()!;

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

    private static async Task DownloadAsync(string url, string dest)
    {
        using var http = NewClient();
        using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        await using Stream src = await resp.Content.ReadAsStreamAsync();
        await using FileStream file = File.Create(dest);
        await src.CopyToAsync(file);
    }

    private static async Task<int> RunAsync(string path, string args)
    {
        // The caller is already elevated, so the installer inherits admin.
        var psi = new ProcessStartInfo { FileName = path, Arguments = args, UseShellExecute = false };
        using Process? p = Process.Start(psi);
        if (p == null) return -1;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TOA-FanControl");
        return http;
    }
}
