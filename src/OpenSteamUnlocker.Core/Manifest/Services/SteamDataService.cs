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
        var result = await TryDepotsAsync($"https://api.9178666.xyz/cmd/{appId}", appId, CaiHeaders, ct);
        if (result.Count > 0) return result;

        result = await TryDepotsAsync($"https://api.steamcmd.net/v1/info/{appId}", appId, null, ct);
        return result;
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
            if (!prop.Value.TryGetProperty("manifests", out var manifests)) continue;
            if (!manifests.TryGetProperty("public", out var pub)) continue;
            if (pub.ValueKind != JsonValueKind.Object) continue;
            if (!pub.TryGetProperty("gid", out var gidEl)) continue;

            var manifestId = GetString(gidEl);
            if (string.IsNullOrEmpty(manifestId)) continue;

            long size = 0;
            if (pub.TryGetProperty("download", out var dlEl))
                size = dlEl.TryGetInt64(out var s) ? s : 0;

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
        var fallback = new GameInfo(appId, string.Empty,
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg");

        using var resp = await _http.GetWithRetryAsync(
            $"https://api.9178666.xyz/cmd/{appId}", timeoutSeconds: 20, maxRetries: 2,
            headers: CaiHeaders, ct: ct);
        if (resp is null) return fallback;

        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl)) return fallback;

            // CaiGames may nest under appId or serve flat
            JsonElement appEl;
            if (!dataEl.TryGetProperty(appId, out appEl))
                appEl = dataEl;

            var name  = appEl.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var image = appEl.TryGetProperty("header_image", out var img)
                ? img.GetString()
                : fallback.HeaderImageUrl;
            var desc  = appEl.TryGetProperty("short_description", out var d) ? d.GetString() : null;

            return new GameInfo(appId, name, image ?? fallback.HeaderImageUrl, desc);
        }
        catch { return fallback; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? GetString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.GetRawText(),
        _ => null,
    };
}
