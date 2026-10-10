using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace FanControlApp.Infrastructure;

/// <summary>Shared HTTP client setup, file download and installer run used by the updaters.</summary>
public static class Downloads
{
    /// <summary>An HttpClient with the app's user agent and the given timeout.</summary>
    public static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TOA-FanControl");
        return http;
    }

    /// <summary>Streams a URL to a file, throwing on any HTTP error.</summary>
    public static async Task DownloadToFileAsync(string url, string dest, TimeSpan timeout)
    {
        using HttpClient http = NewClient(timeout);
        using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        await using Stream src = await resp.Content.ReadAsStreamAsync();
        await using FileStream file = File.Create(dest);
        await src.CopyToAsync(file);
    }

    /// <summary>Runs an installer without the shell and returns its exit code, or -1 if it never started.</summary>
    public static async Task<int> RunAndWaitAsync(string path, string args = "")
    {
        // The caller is already elevated, so the installer inherits admin.
        var psi = new ProcessStartInfo { FileName = path, Arguments = args, UseShellExecute = false };
        using Process? p = Process.Start(psi);
        if (p == null) return -1;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
}
