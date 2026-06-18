using System.Text.Json;
using OpenSteamUnlocker.Core.Http;

namespace OpenSteamUnlocker.Core.Manifest.Services;

/// <summary>
/// Downloads the Sudama full-key database (<c>api.993499094.xyz/depotkeys.json</c>)
/// and caches it locally for 24 hours. The JSON is a flat <c>{"depotId": "key"}</c>
/// dictionary containing tens of thousands of entries.
/// </summary>
public sealed class DepotKeyService : IDepotKeyService
{
    private const string KeysUrl      = "https://api.993499094.xyz/depotkeys.json";
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

        // Download with exponential back-off (mirrors Python's 3-retry logic).
        var delay = TimeSpan.FromSeconds(5);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(delay, ct).ConfigureAwait(false);
            delay = delay + delay; // 5 → 10 → 20 s

            using var resp = await _http.GetWithRetryAsync(
                KeysUrl, timeoutSeconds: 60, maxRetries: 1, ct: ct);
            if (resp is null) continue;

            try
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
                if (data is null || data.Count == 0) continue;

                await SaveCacheAsync(body, ct);
                _cache = data;
                return _cache;
            }
            catch { /* try next attempt */ }
        }

        // Fall back to stale cache if the download failed.
        if (File.Exists(_cacheFile))
        {
            var stale = await TryLoadCacheAsync(ct, ignoreExpiry: true);
            if (stale is not null)
            {
                _cache = stale;
                return _cache;
            }
        }

        _cache = new Dictionary<string, string>();
        return _cache;
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

    private async Task SaveCacheAsync(string dataJson, CancellationToken ct)
    {
        try
        {
            var envelope = $"{{\"Timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()},\"Data\":{dataJson}}}";
            await File.WriteAllTextAsync(_cacheFile, envelope, ct);
        }
        catch { /* non-fatal */ }
    }
}
