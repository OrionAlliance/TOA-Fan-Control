using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace FanControlApp.Infrastructure;

/// <summary>GPU model to reference watts; fetched whole (no per-card query, for privacy), matched locally.</summary>
public static class GpuLibrary
{
    private const string LibraryUrl =
        "https://raw.githubusercontent.com/OrionAlliance/TOA-Fan-Control/main/gpu_library.json";

    private static string CacheFile => Path.Combine(AppPaths.SettingsDir, "gpu_library.json");

    private static Dictionary<string, int>? _gpus;
    private static string? _updated;

    /// <summary>False in processes that never load it, such as the watchdog.</summary>
    public static bool IsLoaded => _gpus != null;

    /// <summary>Loads the cached library (no network); call before the hardware opens.</summary>
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

    /// <summary>Refreshes from GitHub, one shared download; true when a fresh copy landed.</summary>
    public static Task<bool> RefreshAsync()
    {
        if (_refresh == null || _refresh.IsCompleted) _refresh = RefreshCoreAsync();
        return _refresh;
    }

    private static Task<bool>? _refresh;

    private static async Task<bool> RefreshCoreAsync()
    {
        string json;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TOA-FanControl");
            json = await http.GetStringAsync(LibraryUrl);
            Parse(json); // parse first so a bad download never clobbers a good cache
        }
        catch (Exception ex)
        {
            DebugLog.Write("GPU library: refresh failed (offline?) - cached copy stays.", ex);
            return false;
        }

        // Already live in memory; a failed cache write only costs the next launch.
        try
        {
            AppPaths.EnsureSettingsDir();
            File.WriteAllText(CacheFile, json);
        }
        catch (Exception ex)
        {
            DebugLog.Write("GPU library: fresh copy loaded, but caching it failed - next launch reuses the old cache.", ex);
        }

        DebugLog.Write($"GPU library: refreshed from GitHub - {_gpus?.Count ?? 0} cards (updated {_updated ?? "?"}).");
        return true;
    }

    // A match followed by one of these is a different card ("RX 6700 XT" is not "RX 6700").
    private static readonly string[] VariantTokens =
        { "XT", "XTX", "Ti", "SUPER", "GRE", "D", "M", "S", "Laptop", "Mobile", "Max-Q" };

    /// <summary>Reference max watts for this card, or null; the longest matching key wins.</summary>
    public static int? MaxWattsFor(string? cardName)
    {
        var gpus = _gpus; // snapshot, Parse swaps the field wholesale
        if (cardName == null || gpus == null) return null;
        string? best = null;
        foreach (string key in gpus.Keys)
        {
            int at = cardName.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            // Skip partial model numbers ("RX 550" must not claim "RX 5500").
            int end = at + key.Length;
            if (end < cardName.Length && char.IsDigit(cardName[end])) continue;

            // A variant token after the match disqualifies it.
            string rest = cardName[end..].TrimStart();
            string firstWord = rest.Split(' ', 2)[0];
            if (VariantTokens.Any(v => firstWord.Equals(v, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (best == null || key.Length > best.Length) best = key;
        }
        return best == null ? null : gpus[best];
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
