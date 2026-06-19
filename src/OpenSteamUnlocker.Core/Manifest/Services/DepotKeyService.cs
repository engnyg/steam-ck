using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSteamUnlocker.Core.Http;

namespace OpenSteamUnlocker.Core.Manifest.Services;

/// <summary>
/// Downloads the full depot-key database from multiple sources (Sudama + ManifestHub)
/// and caches it locally for 24 hours. The JSON is a flat <c>{"depotId": "key"}</c>
/// dictionary containing tens of thousands of entries.
/// </summary>
public sealed class DepotKeyService : IDepotKeyService
{
    // Sources tried in order; first successful response wins for each depot.
    private static readonly string[] KeySources =
    [
        "https://api.993499094.xyz/depotkeys.json",
        "https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/depotkeys.json",
        "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/main/depotkeys.json",
    ];

    private const double CacheTtlSecs = 86400; // 24 h

    private readonly HttpClient _http;
    private readonly string     _cacheFile;

    // In-memory cache to avoid repeated disk reads within the same session.
    private IReadOnlyDictionary<string, string>? _cache;

    public DepotKeyService(HttpClient http)
    {
        _http = http;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSteamUnlocker", "cache");
        Directory.CreateDirectory(dir);
        _cacheFile = Path.Combine(dir, "depotkeys.json");
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllKeysAsync(CancellationToken ct = default)
    {
        if (_cache is not null) return _cache;

        // Try disk cache first.
        var fromDisk = await TryLoadCacheAsync(ct);
        if (fromDisk is not null)
        {
            _cache = fromDisk;
            return _cache;
        }

        // Download from all sources and merge (earlier sources take priority).
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var url in KeySources)
        {
            var data = await TryDownloadSourceAsync(url, ct);
            if (data is null) continue;
            foreach (var (k, v) in data)
            {
                if (!merged.ContainsKey(k))
                    merged[k] = v;
            }
        }

        if (merged.Count > 0)
        {
            // Persist only the first (primary) source for the cache envelope.
            _cache = merged;
            await SaveCacheAsync(merged, ct);
            return _cache;
        }

        // Fall back to stale cache if all downloads failed.
        var stale = await TryLoadCacheAsync(ct, ignoreExpiry: true);
        if (stale is not null)
        {
            _cache = stale;
            return _cache;
        }

        _cache = new Dictionary<string, string>();
        return _cache;
    }

    private async Task<IReadOnlyDictionary<string, string>?> TryDownloadSourceAsync(
        string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var resp = await _http.GetWithRetryAsync(
                url, timeoutSeconds: 60, maxRetries: 1, ct: ct);
            if (resp is null) continue;
            try
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var data = ParseDepotKeysJson(body);
                if (data is { Count: > 0 }) return data;
            }
            catch { /* try next attempt */ }
        }
        return null;
    }

    public async Task<string?> GetKeyAsync(string depotId, CancellationToken ct = default)
    {
        var all = await GetAllKeysAsync(ct);
        return all.TryGetValue(depotId, out var key) ? key : null;
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private sealed record CacheEnvelope(double Timestamp, Dictionary<string, string> Data);

    private async Task<IReadOnlyDictionary<string, string>?> TryLoadCacheAsync(
        CancellationToken ct, bool ignoreExpiry = false)
    {
        if (!File.Exists(_cacheFile)) return null;
        try
        {
            var json     = await File.ReadAllTextAsync(_cacheFile, ct);
            var envelope = JsonSerializer.Deserialize<CacheEnvelope>(json);
            if (envelope is null || envelope.Data.Count == 0) return null;
            if (!ignoreExpiry && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - envelope.Timestamp > CacheTtlSecs)
                return null;
            return envelope.Data;
        }
        catch { return null; }
    }

    // Parses {"depotId": "hexKey"} — tolerates numeric values in JSON.
    private static IReadOnlyDictionary<string, string>? ParseDepotKeysJson(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject;
        if (node is null) return null;

        var result = new Dictionary<string, string>(node.Count, StringComparer.Ordinal);
        foreach (var (k, v) in node)
        {
            var val = v?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(val))
                result[k] = val;
        }
        return result.Count == 0 ? null : result;
    }

    private async Task SaveCacheAsync(Dictionary<string, string> data, CancellationToken ct)
    {
        try
        {
            var envelope = new CacheEnvelope(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), data);
            var json = JsonSerializer.Serialize(envelope);
            await File.WriteAllTextAsync(_cacheFile, json, ct);
        }
        catch { /* non-fatal */ }
    }
}
