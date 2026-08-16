using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace FanControlApp.Infrastructure;

/// <summary>
/// The GPU power library behind the true-load gauge: model name -> reference
/// board power in watts. The WHOLE file is downloaded from the app's GitHub
/// repo - never a per-card query, so the request carries nothing about this PC
/// - and cached in Settings\. The lookup itself always runs locally.
/// </summary>
public static class GpuLibrary
{
    private const string LibraryUrl =
        "https://raw.githubusercontent.com/OrionAlliance/TOA-Fan-Control/main/gpu_library.json";

    private static string CacheFile => Path.Combine(AppPaths.SettingsDir, "gpu_library.json");

    private static Dictionary<string, int>? _gpus;
    private static string? _updated;

    /// <summary>False in processes that never load it (the watchdog opens the
    /// same hardware but has no business with the library - or its log lines).</summary>
    public static bool IsLoaded => _gpus != null;

    /// <summary>Load the cached library - no network. Call once at startup,
    /// before the hardware opens, so the match can be logged with discovery.</summary>
    public static void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile))
            {
                DebugLog.Write("GPU library: no cache yet - first fetch rides the next update check.");
                return;
            }
            Parse(File.ReadAllText(CacheFile));
            DebugLog.Write($"GPU library: {_gpus?.Count ?? 0} cards loaded from cache (updated {_updated ?? "?"}).");
        }
        catch (Exception ex)
        {
            _gpus = null;
            DebugLog.Write("GPU library: cache unreadable - will refetch.", ex);
        }
    }

    /// <summary>Refresh the cache from GitHub. Rides the daily update check;
    /// silent on failure - offline is normal and the cached copy keeps working.
    /// Returns true when a fresh copy landed, so the caller can re-match the card.</summary>
    public static async Task<bool> RefreshAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TOA-FanControl");
            string json = await http.GetStringAsync(LibraryUrl);
            Parse(json); // parse FIRST - a bad download must never clobber a good cache
            AppPaths.EnsureSettingsDir();
            File.WriteAllText(CacheFile, json);
            DebugLog.Write($"GPU library: refreshed from GitHub - {_gpus?.Count ?? 0} cards (updated {_updated ?? "?"}).");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write("GPU library: refresh failed (offline?) - cached copy stays.", ex);
            return false;
        }
    }

    // A match that's immediately followed by one of these is a BIGGER variant
    // whose own row is missing - "…RX 6700 XT" must never fall into "RX 6700"'s
    // row and quietly get the wrong watts. No match = the honest popup instead.
    private static readonly string[] VariantTokens = { "XT", "XTX", "Ti", "SUPER", "GRE", "D" };

    /// <summary>Reference max watts for this card, or null if unknown. Longest
    /// library key contained in the sensor-reported name wins - "RTX 3080 Ti"
    /// must never fall into "RTX 3080"'s row.</summary>
    public static int? MaxWattsFor(string? cardName)
    {
        if (cardName == null || _gpus == null) return null;
        string? best = null;
        foreach (string key in _gpus.Keys)
        {
            int at = cardName.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            // What follows the match? A variant token disqualifies it.
            string rest = cardName[(at + key.Length)..].TrimStart();
            string firstWord = rest.Split(' ', 2)[0];
            if (VariantTokens.Any(v => firstWord.Equals(v, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (best == null || key.Length > best.Length) best = key;
        }
        return best == null ? null : _gpus[best];
    }

    private static void Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var gpus = new Dictionary<string, int>();
        foreach (JsonProperty p in doc.RootElement.GetProperty("gpus").EnumerateObject())
            gpus[p.Name] = p.Value.GetInt32();
        _updated = doc.RootElement.TryGetProperty("_updated", out JsonElement u) ? u.GetString() : null;
        _gpus = gpus;
    }
}
