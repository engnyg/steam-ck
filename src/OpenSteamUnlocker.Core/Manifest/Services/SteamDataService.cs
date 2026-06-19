using System.Text.Json;
using OpenSteamUnlocker.Core.Http;
using OpenSteamUnlocker.Core.Manifest.Models;

namespace OpenSteamUnlocker.Core.Manifest.Services;

/// <summary>
/// Fetches depot, DLC and game-info metadata.
/// Priority chain mirrors cai_backend's get_depots_safe / get_dlc_ids_safe:
///   CaiGames (api.9178666.xyz) → SteamCMD (api.steamcmd.net) → Steam Store API.
/// </summary>
public sealed class SteamDataService : ISteamDataService
{
    // Ported from cai_backend: CAIGAMES_HEADERS
    private static readonly Dictionary<string, string> CaiHeaders = new()
    {
        ["X-Client-Auth"] = "CaiGames-pvzcxw",
        ["User-Agent"]    = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
        ["Accept"]        = "application/json",
    };

    private readonly HttpClient _http;

    public SteamDataService(HttpClient http) => _http = http;

    // ── Depots ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SteamDepotData>> GetDepotsAsync(
        string appId, CancellationToken ct = default)
    {
        // api.steamcmd.net — returns full depot info including manifest IDs
        var result = await TryDepotsAsync($"https://api.steamcmd.net/v1/info/{appId}", appId, null, ct);
        if (result.Count > 0) return result;

        // CaiGames fallback
        result = await TryDepotsAsync($"https://api.9178666.xyz/cmd/{appId}", appId, CaiHeaders, ct);
        if (result.Count > 0) return result;

        // manifest.steam.run last resort — compact format, may be unreachable
        result = await TryDepotsFromManifestRunAsync($"https://manifest.steam.run/api/depot/{appId}", ct);
        return result;
    }

    private async Task<IReadOnlyList<SteamDepotData>> TryDepotsFromManifestRunAsync(
        string url, CancellationToken ct)
    {
        using var resp = await _http.GetWithRetryAsync(url, timeoutSeconds: 10, maxRetries: 1, ct: ct);
        if (resp is null) return Array.Empty<SteamDepotData>();
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("depots", out var depotsEl))
                return Array.Empty<SteamDepotData>();

            var results = new List<SteamDepotData>();
            foreach (var item in depotsEl.EnumerateArray())
            {
                var depotId    = item.TryGetProperty("depotid",   out var dEl) ? GetString(dEl) : null;
                var manifestId = item.TryGetProperty("manifestid", out var mEl) ? GetString(mEl) : null;
                if (string.IsNullOrEmpty(depotId) || string.IsNullOrEmpty(manifestId)) continue;
                results.Add(new SteamDepotData(depotId, manifestId, 0, null));
            }
            return results;
        }
        catch { return Array.Empty<SteamDepotData>(); }
    }

    private async Task<IReadOnlyList<SteamDepotData>> TryDepotsAsync(
        string url, string appId, Dictionary<string, string>? headers, CancellationToken ct)
    {
        using var resp = await _http.GetWithRetryAsync(url, timeoutSeconds: 20, maxRetries: 2, headers: headers, ct: ct);
        if (resp is null) return Array.Empty<SteamDepotData>();
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return ParseDepots(doc.RootElement, appId);
        }
        catch { return Array.Empty<SteamDepotData>(); }
    }

    private static IReadOnlyList<SteamDepotData> ParseDepots(JsonElement root, string appId)
    {
        var results = new List<SteamDepotData>();
        if (!root.TryGetProperty("data", out var dataEl)) return results;

        // CaiGames returns data[appId], SteamCMD also returns data[appId]
        if (!dataEl.TryGetProperty(appId, out var appEl))
            appEl = dataEl;

        if (!appEl.TryGetProperty("depots", out var depotsEl)) return results;

        foreach (var prop in depotsEl.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            // Only process numeric depot IDs; skip metadata keys like "branches"
            if (!prop.Name.All(char.IsDigit)) continue;

            // Extract manifest ID from public branch if present (may be absent for DLC depots)
            string? manifestId = null;
            long size = 0;
            if (prop.Value.TryGetProperty("manifests", out var manifests) &&
                manifests.TryGetProperty("public", out var pub) &&
                pub.ValueKind == JsonValueKind.Object)
            {
                if (pub.TryGetProperty("gid", out var gidEl))
                    manifestId = GetString(gidEl);
                if (pub.TryGetProperty("download", out var dlEl))
                {
                    if (dlEl.ValueKind == JsonValueKind.Number)
                        dlEl.TryGetInt64(out size);
                    else if (dlEl.ValueKind == JsonValueKind.String &&
                             long.TryParse(dlEl.GetString(), out var parsed))
                        size = parsed;
                }
            }

            string? dlcAppId = null;
            if (prop.Value.TryGetProperty("dlcappid", out var dlcEl) &&
                dlcEl.ValueKind != JsonValueKind.Null)
                dlcAppId = GetString(dlcEl);

            results.Add(new SteamDepotData(prop.Name, manifestId, size, dlcAppId));
        }
        return results;
    }

    // ── DLC IDs ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetDlcIdsAsync(
        string appId, CancellationToken ct = default)
    {
        var result = await TryDlcIdsAsync($"https://api.9178666.xyz/cmd/{appId}", appId, CaiHeaders, ct);
        if (result.Count > 0) return result;

        result = await TryDlcIdsAsync($"https://api.steamcmd.net/v1/info/{appId}", appId, null, ct);
        if (result.Count > 0) return result;

        // Steam Store API fallback
        foreach (var lang in new[] { "schinese", "english" })
        {
            result = await TryDlcIdsFromStoreAsync(
                $"https://store.steampowered.com/api/appdetails?appids={appId}&l={lang}", appId, ct);
            if (result.Count > 0) return result;
        }
        return Array.Empty<string>();
    }

    private async Task<IReadOnlyList<string>> TryDlcIdsAsync(
        string url, string appId, Dictionary<string, string>? headers, CancellationToken ct)
    {
        using var resp = await _http.GetWithRetryAsync(url, timeoutSeconds: 20, maxRetries: 2, headers: headers, ct: ct);
        if (resp is null) return Array.Empty<string>();
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return ParseDlcIds(doc.RootElement, appId);
        }
        catch { return Array.Empty<string>(); }
    }

    private static IReadOnlyList<string> ParseDlcIds(JsonElement root, string appId)
    {
        if (!root.TryGetProperty("data", out var dataEl)) return Array.Empty<string>();

        // Handles both CaiGames (data = game object) and SteamCMD (data[appId] = game object)
        JsonElement appEl;
        if (!dataEl.TryGetProperty(appId, out appEl))
            appEl = dataEl;

        string? dlcStr = null;
        if (appEl.TryGetProperty("extended", out var ext) && ext.TryGetProperty("listofdlc", out var d1))
            dlcStr = d1.GetString();
        if (string.IsNullOrEmpty(dlcStr) &&
            appEl.TryGetProperty("common", out var common) && common.TryGetProperty("listofdlc", out var d2))
            dlcStr = d2.GetString();

        if (string.IsNullOrEmpty(dlcStr)) return Array.Empty<string>();

        return dlcStr.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && s.All(char.IsDigit))
            .OrderBy(long.Parse)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> TryDlcIdsFromStoreAsync(
        string url, string appId, CancellationToken ct)
    {
        using var resp = await _http.GetWithRetryAsync(url, timeoutSeconds: 25, maxRetries: 2, ct: ct);
        if (resp is null) return Array.Empty<string>();
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(appId, out var appEl)) return Array.Empty<string>();
            if (!appEl.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return Array.Empty<string>();
            if (!appEl.TryGetProperty("data", out var data)) return Array.Empty<string>();
            if (!data.TryGetProperty("dlc", out var dlcArr)) return Array.Empty<string>();

            var ids = new List<string>();
            foreach (var item in dlcArr.EnumerateArray())
            {
                var id = GetString(item);
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            return ids;
        }
        catch { return Array.Empty<string>(); }
    }

    // ── Game info ─────────────────────────────────────────────────────────────

    public async Task<GameInfo> GetGameInfoAsync(string appId, CancellationToken ct = default)
    {
        var imageUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";

        // Try SteamCMD first — reliable name + image URL.
        var info = await TryGameInfoFromSteamCmdAsync(appId, imageUrl, ct);
        if (info is not null) return info;

        // Fall back to CaiGames.
        info = await TryGameInfoFromCaiAsync(appId, imageUrl, ct);
        if (info is not null) return info;

        return new GameInfo(appId, appId, imageUrl);
    }

    private async Task<GameInfo?> TryGameInfoFromSteamCmdAsync(
        string appId, string fallbackImageUrl, CancellationToken ct)
    {
        using var resp = await _http.GetWithRetryAsync(
            $"https://api.steamcmd.net/v1/info/{appId}", timeoutSeconds: 15, maxRetries: 1, ct: ct);
        if (resp is null) return null;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl)) return null;
            if (!dataEl.TryGetProperty(appId, out var appEl)) return null;

            string name = string.Empty;
            if (appEl.TryGetProperty("common", out var common) &&
                common.TryGetProperty("name", out var n))
                name = n.GetString() ?? string.Empty;

            if (string.IsNullOrEmpty(name)) return null;
            return new GameInfo(appId, name, fallbackImageUrl);
        }
        catch { return null; }
    }

    private async Task<GameInfo?> TryGameInfoFromCaiAsync(
        string appId, string fallbackImageUrl, CancellationToken ct)
    {
        using var resp = await _http.GetWithRetryAsync(
            $"https://api.9178666.xyz/cmd/{appId}", timeoutSeconds: 15, maxRetries: 1,
            headers: CaiHeaders, ct: ct);
        if (resp is null) return null;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl)) return null;
            JsonElement appEl;
            if (!dataEl.TryGetProperty(appId, out appEl)) appEl = dataEl;

            var name  = appEl.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(name)) return null;

            var image = appEl.TryGetProperty("header_image", out var img)
                ? img.GetString() ?? fallbackImageUrl : fallbackImageUrl;
            return new GameInfo(appId, name, image);
        }
        catch { return null; }
    }

    // ── Image download ────────────────────────────────────────────────────────

    public async Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct = default)
    {
        using var resp = await _http.GetWithRetryAsync(url, timeoutSeconds: 15, maxRetries: 1, ct: ct);
        if (resp is null) return null;
        try { return await resp.Content.ReadAsByteArrayAsync(ct); }
        catch { return null; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? GetString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.GetRawText(),
        _ => null,
    };
}
