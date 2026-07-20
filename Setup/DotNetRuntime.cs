using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using FanControlApp.Cooling;      // reuse PawnIoSetup.IsTrustedAndSignedBy
using FanControlApp.Infrastructure;

namespace FanControlSetup;

/// <summary>
/// Detects and installs the .NET 10 Desktop Runtime - the one prerequisite the app
/// itself can't handle, because without .NET the app never starts. We fetch the
/// version and installer URL from Microsoft's official release metadata (so it's
/// always the current patch), verify the download is Microsoft-signed, and run it.
/// </summary>
public static class DotNetRuntime
{
    private const string ReleasesIndex =
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";

    // The app targets net10.0-windows, so it needs the WindowsDesktop shared
    // framework, major version 10.
    private const string DesktopFramework = "Microsoft.WindowsDesktop.App";
    private const int RequiredMajor = 10;

    public sealed record Result(bool Success, bool RebootRequired, string Message);

    /// <summary>
    /// Is a 10.x WindowsDesktop runtime present? Checks the shared-framework folder
    /// under every dotnet root Windows knows about.
    /// </summary>
    public static bool IsInstalled()
    {
        foreach (string root in DotnetRoots())
        {
            string dir = Path.Combine(root, "shared", DesktopFramework);
            if (!Directory.Exists(dir)) continue;

            foreach (string ver in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(ver);
                if (int.TryParse(name.Split('.').FirstOrDefault(), out int major) && major >= RequiredMajor)
                    return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> DotnetRoots()
    {
        // Default machine-wide install, plus whatever DOTNET_ROOT points at.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");

        string? envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot)) yield return envRoot;
    }

    public static async Task<Result> InstallAsync(IProgress<string> progress)
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
                return new Result(false, false,
                    "The .NET download didn't pass Microsoft's signature check, so it wasn't run.");
            }

            progress.Report("Installing .NET 10…");
            int exit = await RunAsync(temp, "/install /quiet /norestart");

            return exit switch
            {
                0 => new Result(true, false, ".NET 10 installed."),
                3010 => new Result(true, true, ".NET 10 installed - a reboot will finish it."),
                _ => new Result(false, false, $"The .NET installer exited with code {exit}."),
            };
        }
        catch (Exception ex)
        {
            DebugLog.Write(".NET runtime install failed.", ex);
            return new Result(false, false,
                "Couldn't install .NET 10 automatically. " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp cleanup */ }
        }
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
        // Setup is already elevated, so the installer inherits admin - no extra UAC.
        var psi = new ProcessStartInfo { FileName = path, Arguments = args, UseShellExecute = false };
        using Process? p = Process.Start(psi);
        if (p == null) return -1;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TOA-FanControl-Setup");
        return http;
    }
}
