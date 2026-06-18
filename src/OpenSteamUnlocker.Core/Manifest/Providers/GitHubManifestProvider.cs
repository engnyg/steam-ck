using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValveKeyValue;
using OpenSteamUnlocker.Core.Http;

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
public sealed partial class GitHubManifestProvider : IGitHubManifestProvider
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
            // If null is returned due to a missing branch (not rate-limit), the
            // _apiExhausted flag was not set, so callers can try other repos.
        }

        // API exhausted → fallback to ZIP
        if (_apiExhausted)
            return await TryViaZipAsync(appId, repo, skipManifestFiles, ct);

        return null;
    }

    // ── API path ──────────────────────────────────────────────────────────────

    private async Task<GitHubManifestData?> TryViaApiAsync(
        string appId, string repo, string? token, bool skipManifests, CancellationToken ct)
    {
        var headers = BuildAuthHeaders(token);

        // 1. Branch info → commit SHA
        var branchJson = await ApiGetAsync(
            $"https://api.github.com/repos/{repo}/branches/{appId}", headers, ct);
        if (branchJson is null) return null; // branch not found or API exhausted

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

        // 2. File tree
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

        // 3. Download files via mirrors (parallel, same as Python's asyncio.gather)
        var downloads = await Task.WhenAll(
            filePaths.Select(path => DownloadFileAsync(repo, commitSha, path, ct)));

        return BuildResult(filePaths, downloads, skipManifests);
    }

    // ── ZIP fallback ──────────────────────────────────────────────────────────

    private async Task<GitHubManifestData?> TryViaZipAsync(
        string appId, string repo, bool skipManifests, CancellationToken ct)
    {
        var zipUrl = $"https://codeload.github.com/{repo}/zip/refs/heads/{appId}";
        using var resp = await _http.GetWithRetryAsync(zipUrl, timeoutSeconds: 60, maxRetries: 2, ct: ct);
        if (resp is null) return null;

        try
        {
            var bytes  = await resp.Content.ReadAsByteArrayAsync(ct);
            return ExtractZip(bytes, skipManifests);
        }
        catch { return null; }
    }

    private static GitHubManifestData? ExtractZip(byte[] zipBytes, bool skipManifests)
    {
        var depotKeys     = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestFiles = new List<GitHubManifestFile>();

        using var ms      = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            // Skip directories
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var name = entry.Name;

            if (name.Equals("key.vdf", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = entry.Open();
                var keys = ParseKeyVdf(stream);
                foreach (var (k, v) in keys) depotKeys[k] = v;
                continue;
            }

            if (!skipManifests && name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
            {
                var m = ManifestFilenameRegex().Match(name);
                if (!m.Success) continue;
                using var stream = entry.Open();
                using var buf    = new MemoryStream();
                stream.CopyTo(buf);
                manifestFiles.Add(new GitHubManifestFile(m.Groups[1].Value, m.Groups[2].Value, buf.ToArray()));
            }
        }

        if (depotKeys.Count == 0 && manifestFiles.Count == 0) return null;
        return new GitHubManifestData(depotKeys, manifestFiles);
    }

    // ── Result builder ────────────────────────────────────────────────────────

    private static GitHubManifestData BuildResult(
        List<string> paths, byte[]?[] downloads, bool skipManifests)
    {
        var depotKeys     = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestFiles = new List<GitHubManifestFile>();

        for (var i = 0; i < paths.Count; i++)
        {
            if (downloads[i] is null) continue;
            var name = Path.GetFileName(paths[i]);

            if (name.Equals("key.vdf", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new MemoryStream(downloads[i]!);
                var keys = ParseKeyVdf(stream);
                foreach (var (k, v) in keys) depotKeys[k] = v;
                continue;
            }

            if (!skipManifests && name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
            {
                var m = ManifestFilenameRegex().Match(name);
                if (!m.Success) continue;
                manifestFiles.Add(new GitHubManifestFile(m.Groups[1].Value, m.Groups[2].Value, downloads[i]!));
            }
        }

        if (depotKeys.Count == 0 && manifestFiles.Count == 0) return null!;
        return new GitHubManifestData(depotKeys, manifestFiles);
    }

    // ── key.vdf parser ────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, string> ParseKeyVdf(Stream stream)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var kv       = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
            var document = kv.Deserialize(stream);
            // Root = "depots"; each child = depot entry with "DecryptionKey"
            foreach (var entry in document.Root.Children)
            {
                var key = entry.Value["DecryptionKey"]
                    ?.ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(key))
                    result[entry.Key] = key;
            }
        }
        catch { /* malformed VDF — return partial */ }
        return result;
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<string?> ApiGetAsync(
        string url, Dictionary<string, string> headers, CancellationToken ct)
    {
        using var resp = await _http.GetWithRetryAsync(
            url, timeoutSeconds: 30, maxRetries: 1, headers: headers, ct: ct);

        if (resp is null) return null;

        // 401 / 403 / 429 → rate limited or unauthenticated
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
        try
        {
            return await _mirrors.GetFromMirrorsAsync(repo, sha, path, ct);
        }
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

    [GeneratedRegex(@"^(\d+)_(\w+)\.manifest$", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestFilenameRegex();
}
