using System.Text.Json;
using System.Text.RegularExpressions;
using OpenSteamUnlocker.Core.Http;
using OpenSteamUnlocker.Core.Manifest.Models;

namespace OpenSteamUnlocker.Core.Manifest.Services;

/// <summary>
/// Game search and info lookup. Priority: CaiGames API → Steam Store search.
/// Mirrors cai_backend's <c>find_appid_by_name</c>.
/// </summary>
public sealed partial class GameSearchService : IGameSearchService
{
    private static readonly Dictionary<string, string> CaiHeaders = new()
    {
        ["X-Client-Auth"] = "CaiGames-pvzcxw",
        ["User-Agent"]    = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
        ["Accept"]        = "application/json",
    };

    private readonly HttpClient _http;

    public GameSearchService(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<GameInfo>> SearchAsync(string term, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term)) return Array.Empty<GameInfo>();

        // 1. CaiGames search API
        var encoded = Uri.EscapeDataString(term);
        using var caiResp = await _http.GetWithRetryAsync(
            $"https://api.9178666.xyz/search?term={encoded}",
            timeoutSeconds: 20, maxRetries: 2, headers: CaiHeaders, ct: ct);

        if (caiResp is not null)
        {
            try
            {
                var body = await caiResp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var results = ParseCaiSearchResults(doc.RootElement);
                if (results.Count > 0) return results;
            }
            catch { /* fall through */ }
        }

        // 2. Steam Store search fallback (HTML scrape, same as Python fallback)
        return await SteamStoreSearchAsync(term, ct);
    }

    public string? ExtractAppId(string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return null;
        var trimmed = userInput.Trim();

        // Full Steam store URL: /app/730/...
        var m = StoreUrlRegex().Match(trimmed);
        if (m.Success) return m.Groups[1].Value;

        // SteamDB URL
        m = SteamDbRegex().Match(trimmed);
        if (m.Success) return m.Groups[1].Value;

        // Bare numeric
        if (trimmed.All(char.IsDigit) && trimmed.Length > 0) return trimmed;

        return null;
    }

    // ── CaiGames response parser ───────────────────────────────────────────────

    private static IReadOnlyList<GameInfo> ParseCaiSearchResults(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)) return Array.Empty<GameInfo>();
        if (data.ValueKind != JsonValueKind.Array) return Array.Empty<GameInfo>();

        var results = new List<GameInfo>();
        foreach (var item in data.EnumerateArray())
        {
            string? appId = null, name = null, image = null;

            if (item.TryGetProperty("appid", out var aid))
                appId = aid.ValueKind == JsonValueKind.Number
                    ? aid.GetInt64().ToString()
                    : aid.GetString();

            if (item.TryGetProperty("name", out var n))
                name = n.GetString();

            if (item.TryGetProperty("image", out var img))
                image = img.GetString();

            if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(name))
                results.Add(new GameInfo(appId, name, image));
        }
        return results;
    }

    // ── Steam Store HTML search ────────────────────────────────────────────────

    private async Task<IReadOnlyList<GameInfo>> SteamStoreSearchAsync(string term, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(term);
        var url     = $"https://store.steampowered.com/search/?term={encoded}&supportedlang=schinese&ndl=1";

        var htmlHeaders = new Dictionary<string, string>
        {
            ["User-Agent"]      = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
            ["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.8",
        };

        using var resp = await _http.GetWithRetryAsync(url, timeoutSeconds: 30, maxRetries: 2,
            headers: htmlHeaders, ct: ct);
        if (resp is null) return Array.Empty<GameInfo>();

        var html    = await resp.Content.ReadAsStringAsync(ct);
        var results = new List<GameInfo>();

        foreach (Match game in SteamSearchResultRegex().Matches(html))
        {
            var appId    = game.Groups[1].Value;
            var titleM   = TitleSpanRegex().Match(game.Groups[2].Value);
            if (!titleM.Success) continue;

            var name  = titleM.Groups[1].Value.Trim();
            var image = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
            results.Add(new GameInfo(appId, name, image));
            if (results.Count >= 20) break;
        }
        return results;
    }

    // ── Source-generated regexes ──────────────────────────────────────────────

    [GeneratedRegex(@"/app/(\d+)/")]
    private static partial Regex StoreUrlRegex();

    [GeneratedRegex(@"steamdb\.info/app/(\d+)")]
    private static partial Regex SteamDbRegex();

    [GeneratedRegex(@"<a href=""https://store\.steampowered\.com/app/(\d+)/[^""]*""[^>]*>(.*?)</a>",
        RegexOptions.Singleline)]
    private static partial Regex SteamSearchResultRegex();

    [GeneratedRegex(@"<span class=""title"">(.*?)</span>")]
    private static partial Regex TitleSpanRegex();
}
