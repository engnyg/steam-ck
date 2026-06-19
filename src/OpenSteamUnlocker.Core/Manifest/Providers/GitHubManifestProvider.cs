using System.Text.Json;
using OpenSteamUnlocker.Core.Http;
using OpenSteamUnlocker.Core.Manifest;

namespace OpenSteamUnlocker.Core.Manifest.Providers;

/// <summary>
/// Downloads manifests from a GitHub repo whose branches are named by AppID
/// (the SteamAutoCracks/ManifestHub convention).
///
/// Flow:
///   1. GET /repos/{repo}/branches/{appId}   → commit SHA
///   2. GET /git/trees/{sha}?recursive=1     → file list
///   3. Download each file via CN mirrors     → parse key.vdf + collect *.manifest
///   4. On API-exhaustion (403/429): fall back to branch ZIP download.
/// </summary>
public sealed class GitHubManifestProvider : IGitHubManifestProvider
{
    private readonly HttpClient          _http;
    private readonly IGitHubMirrorClient _mirrors;

    // Tracks whether the GitHub API is exhausted for this session.
    private bool _apiExhausted;

    public GitHubManifestProvider(HttpClient http, IGitHubMirrorClient mirrors)
    {
        _http    = http;
        _mirrors = mirrors;
    }

    public async Task<GitHubManifestData?> FetchAsync(
        string appId,
        string repo,
        string? githubToken      = null,
        bool   skipManifestFiles = false,
        CancellationToken ct     = default)
    {
        if (!_apiExhausted)
        {
            var result = await TryViaApiAsync(appId, repo, githubToken, skipManifestFiles, ct);
            if (result is not null) return result;
        }

        if (_apiExhausted)
            return await TryViaZipAsync(appId, repo, skipManifestFiles, ct);

        return null;
    }

    // ── API path ──────────────────────────────────────────────────────────────

    private async Task<GitHubManifestData?> TryViaApiAsync(
        string appId, string repo, string? token, bool skipManifests, CancellationToken ct)
    {
        var headers = BuildAuthHeaders(token);

        var branchJson = await ApiGetAsync(
            $"https://api.github.com/repos/{repo}/branches/{appId}", headers, ct);
        if (branchJson is null) return null;

        string commitSha, treeUrl;
        try
        {
            using var doc = JsonDocument.Parse(branchJson);
            commitSha = doc.RootElement
                .GetProperty("commit").GetProperty("sha").GetString()!;
            treeUrl = doc.RootElement
                .GetProperty("commit").GetProperty("commit")
                .GetProperty("tree").GetProperty("url").GetString()!;
        }
        catch { return null; }

        var treeJson = await ApiGetAsync($"{treeUrl}?recursive=1", headers, ct);
        if (treeJson is null) return null;

        List<string> filePaths;
        try
        {
            using var doc = JsonDocument.Parse(treeJson);
            filePaths = doc.RootElement.GetProperty("tree").EnumerateArray()
                .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "blob")
                .Select(e => e.GetProperty("path").GetString()!)
                .ToList();
        }
        catch { return null; }

        if (filePaths.Count == 0) return null;

        if (skipManifests)
            filePaths = filePaths.Where(p => !p.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)).ToList();

        var downloads = await Task.WhenAll(
            filePaths.Select(path => DownloadFileAsync(repo, commitSha, path, ct)));

        return ManifestZipExtractor.ExtractFromFiles(filePaths, downloads, skipManifests);
    }

    // ── ZIP fallback ──────────────────────────────────────────────────────────

    private async Task<GitHubManifestData?> TryViaZipAsync(
        string appId, string repo, bool skipManifests, CancellationToken ct)
    {
        try
        {
            var bytes = await _mirrors.GetBranchZipAsync(repo, appId, ct);
            if (bytes is null) return null;
            return ManifestZipExtractor.Extract(bytes, skipManifests);
        }
        catch { return null; }
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<string?> ApiGetAsync(
        string url, Dictionary<string, string> headers, CancellationToken ct)
    {
        using var resp = await _http.GetWithRetryAsync(
            url, timeoutSeconds: 30, maxRetries: 1, headers: headers, ct: ct);

        if (resp is null) return null;

        var status = (int)resp.StatusCode;
        if (status is 401 or 403 or 429)
        {
            _apiExhausted = true;
            return null;
        }

        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<byte[]?> DownloadFileAsync(
        string repo, string sha, string path, CancellationToken ct)
    {
        try { return await _mirrors.GetFromMirrorsAsync(repo, sha, path, ct); }
        catch { return null; }
    }

    private static Dictionary<string, string> BuildAuthHeaders(string? token)
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = "OpenSteamUnlocker",
            ["Accept"]     = "application/vnd.github+json",
        };
        if (!string.IsNullOrEmpty(token))
            headers["Authorization"] = $"Bearer {token}";
        return headers;
    }
}
