using System.Net.Http;
using System.Text;
using OpenSteamUnlocker.Core.Engine;
using OpenSteamUnlocker.Core.Manifest.Lua;
using OpenSteamUnlocker.Core.Manifest.Models;
using OpenSteamUnlocker.Core.Manifest.Providers;
using OpenSteamUnlocker.Core.Steam;

namespace OpenSteamUnlocker.Core.Manifest.Services;

/// <summary>
/// Orchestrates the full manifest install pipeline with multi-source support.
///
/// Sources (in auto-select order):
///   1. SudamaGitHub — Sudama key DB + ManifestAutoUpdate GitHub repos
///   2. Cysaw        — cysaw.pw/proxy POST → ZIP with *.manifest + *.lua keys
///   3. SteamAutoCracks — SteamAutoCracks/ManifestHub per-AppID branch
///
/// Auto mode stops at the first source that yields at least one valid depot key.
/// </summary>
public sealed class ManifestInstaller : IManifestInstaller
{
    // Repos with per-AppID branches (branch name = appId).
    private static readonly IReadOnlyList<string> DefaultRepos =
    [
        "Auiowu/ManifestAutoUpdate",
        "Satisl/MAU",
        "BlankTMing/ManifestAutoUpdate",
        "tymolu233/ManifestAutoUpdate-fix",
        "isKoi/ManifestAutoUpdate",
        "pjy612/SteamManifestCache",
        "ikun0014/ManifestHub",
    ];

    private readonly IGitHubManifestProvider _github;
    private readonly ISteamDataService       _steamData;
    private readonly IDepotKeyService        _depotKeys;
    private readonly IEngineService          _engine;
    private readonly ISteamLocator           _locator;
    private readonly HttpClient              _http;

    public ManifestInstaller(
        IGitHubManifestProvider github,
        ISteamDataService       steamData,
        IDepotKeyService        depotKeys,
        IEngineService          engine,
        ISteamLocator           locator,
        HttpClient              http)
    {
        _github    = github;
        _steamData = steamData;
        _depotKeys = depotKeys;
        _engine    = engine;
        _locator   = locator;
        _http      = http;
    }

    public async Task<ManifestInstallResult> InstallAsync(
        string appId,
        ManifestInstallOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return Fail(appId, "AppID 不能為空。");

        if (!_locator.IsInstalled)
            return Fail(appId, "找不到 Steam 安裝。");

        CollectedData? data;

        if (options.SourceMode == ManifestSourceMode.Auto)
        {
            data = null;
            foreach (var collect in new Func<string, ManifestInstallOptions, IProgress<string>?, CancellationToken, Task<CollectedData?>>[]
            {
                CollectSudamaGitHubAsync,
                CollectCysawAsync,
                CollectSteamAutoCracksAsync,
            })
            {
                data = await collect(appId, options, progress, ct);
                if (data is not null && data.Depots.Any(d => IsValidDepotKey(d.DecryptionKey)))
                    break;
            }
        }
        else
        {
            data = options.SourceMode switch
            {
                ManifestSourceMode.SudamaGitHub    => await CollectSudamaGitHubAsync(appId, options, progress, ct),
                ManifestSourceMode.Cysaw           => await CollectCysawAsync(appId, options, progress, ct),
                ManifestSourceMode.SteamAutoCracks => await CollectSteamAutoCracksAsync(appId, options, progress, ct),
                _ => null,
            };
        }

        if (data is null)
            return Fail(appId, "所有來源均找不到此遊戲的 depot 資料。");

        return await FinalizeAsync(appId, data, options, progress, ct);
    }

    // ── Source collectors ─────────────────────────────────────────────────────

    private async Task<CollectedData?> CollectSudamaGitHubAsync(
        string appId, ManifestInstallOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("正在載入 Sudama 解密金鑰庫…");
        var allDepotKeys = await _depotKeys.GetAllKeysAsync(ct);
        progress?.Report($"金鑰庫載入完成，共 {allDepotKeys.Count:N0} 個金鑰。");

        progress?.Report("正在從 API 取得遊戲 depot 清單…");
        var apiDepots = await _steamData.GetDepotsAsync(appId, ct);

        var depots = apiDepots
            .Select(d => new DepotInfo(
                d.DepotId,
                allDepotKeys.TryGetValue(d.DepotId, out var k) && IsValidDepotKey(k) ? k : null,
                d.ManifestId))
            .ToList();

        ApplyPatchDepotKey(appId, depots, allDepotKeys);

        var sudamaKeyCount = depots.Count(d => IsValidDepotKey(d.DecryptionKey));
        progress?.Report($"API 找到 {depots.Count} 個 depot，Sudama 提供 {sudamaKeyCount} 個解密金鑰。");

        var repos = new List<string>();
        if (options.ExtraGitHubRepos is { Count: > 0 } extra) repos.AddRange(extra);
        repos.AddRange(DefaultRepos);

        // Manifest ID dictionary: depotId → (manifestId, content) — updated by every repo.
        // Later repos overwrite earlier ones so the most up-to-date manifest ID wins.
        var manifestById = new Dictionary<string, GitHubManifestFile>(StringComparer.OrdinalIgnoreCase);
        string? gitHubRepo = null;

        foreach (var repo in repos)
        {
            progress?.Report($"正在從 {repo} 取得資料…");
            // Always download manifests (never skip because an earlier repo already succeeded).
            // This ensures that a newer repo (e.g. Satisl/MAU) can overwrite a stale
            // manifest ID obtained from the first repo (e.g. Auiowu/ManifestAutoUpdate).
            bool skipManifests = options.AutoUpdate;
            var ghData = await _github.FetchAsync(
                appId, repo, options.GithubPersonalToken, skipManifestFiles: skipManifests, ct);
            if (ghData is null) continue;

            foreach (var (depotId, vdfKey) in ghData.DepotKeys)
            {
                if (!IsValidDepotKey(vdfKey)) continue;
                var idx = depots.FindIndex(d => d.DepotId == depotId);
                if (idx >= 0 && !IsValidDepotKey(depots[idx].DecryptionKey))
                    depots[idx] = depots[idx] with { DecryptionKey = vdfKey };
                else if (idx < 0)
                    depots.Add(new DepotInfo(depotId, vdfKey, null));
            }

            if (!skipManifests && ghData.ManifestFiles.Count > 0)
            {
                foreach (var mf in ghData.ManifestFiles)
                {
                    // Overwrite any previously collected manifest for this depot.
                    manifestById[mf.DepotId] = mf;

                    var idx = depots.FindIndex(d => d.DepotId == mf.DepotId);
                    if (idx >= 0)
                        depots[idx] = depots[idx] with { ManifestId = mf.ManifestId };
                    else
                        depots.Add(new DepotInfo(mf.DepotId,
                            allDepotKeys.TryGetValue(mf.DepotId, out var mk) && IsValidDepotKey(mk) ? mk : null,
                            mf.ManifestId));
                }
                gitHubRepo = repo;
            }
            else if (gitHubRepo is null && ghData.DepotKeys.Count > 0)
                gitHubRepo = repo;
        }

        // Flatten the manifest map back into a list (deduplicated, newest wins).
        List<GitHubManifestFile> manifestFiles = [.. manifestById.Values];

        if (depots.Count == 0) return null;

        var label = gitHubRepo is not null ? $"Sudama + {gitHubRepo}" : "Sudama API";
        return new CollectedData(depots, manifestFiles, label);
    }

    private async Task<CollectedData?> CollectCysawAsync(
        string appId, ManifestInstallOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("正在從 Cysaw 取得清單…");

        byte[]? zipBytes = null;
        try
        {
            using var body = new StringContent($"{{\"appId\":{appId}}}", Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("https://cysaw.pw/proxy", body, ct);
            if (resp.IsSuccessStatusCode)
                zipBytes = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch { /* treat as unavailable */ }

        if (zipBytes is null) return null;

        var extracted = ManifestZipExtractor.Extract(zipBytes, options.AutoUpdate);
        if (extracted is null) return null;

        var allDepotKeys = await _depotKeys.GetAllKeysAsync(ct);
        var apiDepots    = await _steamData.GetDepotsAsync(appId, ct);

        var depots = apiDepots
            .Select(d => new DepotInfo(
                d.DepotId,
                extracted.DepotKeys.TryGetValue(d.DepotId, out var k1) ? k1 :
                allDepotKeys.TryGetValue(d.DepotId, out var k2) && IsValidDepotKey(k2) ? k2 : null,
                d.ManifestId))
            .ToList();

        foreach (var (depotId, key) in extracted.DepotKeys)
        {
            if (!depots.Any(d => d.DepotId == depotId))
                depots.Add(new DepotInfo(depotId, key, null));
        }

        foreach (var mf in extracted.ManifestFiles)
        {
            var idx = depots.FindIndex(d => d.DepotId == mf.DepotId);
            if (idx >= 0)
                depots[idx] = depots[idx] with { ManifestId = mf.ManifestId };
            else
                depots.Add(new DepotInfo(mf.DepotId, null, mf.ManifestId));
        }

        ApplyPatchDepotKey(appId, depots, allDepotKeys);

        return new CollectedData(depots, extracted.ManifestFiles.ToList(), "Cysaw");
    }

    private async Task<CollectedData?> CollectSteamAutoCracksAsync(
        string appId, ManifestInstallOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("正在從 SteamAutoCracks/ManifestHub 取得清單…");

        var ghData = await _github.FetchAsync(
            appId, "SteamAutoCracks/ManifestHub",
            options.GithubPersonalToken, skipManifestFiles: options.AutoUpdate, ct);

        if (ghData is null) return null;

        var allDepotKeys = await _depotKeys.GetAllKeysAsync(ct);
        var apiDepots    = await _steamData.GetDepotsAsync(appId, ct);

        var depots = apiDepots
            .Select(d => new DepotInfo(
                d.DepotId,
                ghData.DepotKeys.TryGetValue(d.DepotId, out var k1) ? k1 :
                allDepotKeys.TryGetValue(d.DepotId, out var k2) && IsValidDepotKey(k2) ? k2 : null,
                d.ManifestId))
            .ToList();

        foreach (var (depotId, key) in ghData.DepotKeys)
        {
            if (!depots.Any(d => d.DepotId == depotId))
                depots.Add(new DepotInfo(depotId, key, null));
        }

        foreach (var mf in ghData.ManifestFiles)
        {
            var idx = depots.FindIndex(d => d.DepotId == mf.DepotId);
            if (idx >= 0)
                depots[idx] = depots[idx] with { ManifestId = mf.ManifestId };
            else
                depots.Add(new DepotInfo(mf.DepotId,
                    allDepotKeys.TryGetValue(mf.DepotId, out var mk) && IsValidDepotKey(mk) ? mk : null,
                    mf.ManifestId));
        }

        ApplyPatchDepotKey(appId, depots, allDepotKeys);

        return new CollectedData(depots, ghData.ManifestFiles.ToList(), "SteamAutoCracks/ManifestHub");
    }

    // ── Finalization ──────────────────────────────────────────────────────────

    private async Task<ManifestInstallResult> FinalizeAsync(
        string appId, CollectedData data,
        ManifestInstallOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        var depots        = data.Depots;
        var manifestFiles = data.Files;

        var totalKeys = depots.Count(d => IsValidDepotKey(d.DecryptionKey));
        progress?.Report($"共 {totalKeys}/{depots.Count} 個 depot 有解密金鑰，正在寫入 Lua…");

        var lua = LuaManifestBuilder.Build(appId, depots, options.AutoUpdate);

        if (options.AddFreeDlc)
        {
            progress?.Report("正在取得 DLC 清單…");
            var dlcIds = await _steamData.GetDlcIdsAsync(appId, ct);
            if (dlcIds.Count > 0)
            {
                var allKeys = await _depotKeys.GetAllKeysAsync(ct);
                lua = LuaManifestBuilder.AppendFreeDlc(lua, dlcIds, allKeys);
            }
        }

        await _engine.WriteLuaAsync(appId, lua, ct);

        int copied = 0;
        if (!options.AutoUpdate && manifestFiles.Count > 0)
        {
            progress?.Report("正在複製 manifest 檔案到 depotcache…");
            copied = await CopyManifestFilesAsync(manifestFiles, ct);
        }

        progress?.Report("完成。");
        return new ManifestInstallResult(
            Success: true, AppId: appId,
            LuaPath: Path.Combine(_locator.LuaConfigPath!, $"{appId}.lua"),
            ManifestsCopied: copied,
            DlcsAdded: CountDlcLines(lua),
            ProviderUsed: data.Provider);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed record CollectedData(
        List<DepotInfo> Depots,
        List<GitHubManifestFile> Files,
        string Provider);

    private static void ApplyPatchDepotKey(
        string appId, List<DepotInfo> depots, IReadOnlyDictionary<string, string> allKeys)
    {
        if (!allKeys.TryGetValue(appId, out var key) || !IsValidDepotKey(key)) return;
        var idx = depots.FindIndex(d => d.DepotId == appId);
        if (idx < 0)
            depots.Add(new DepotInfo(appId, key, null));
        else if (!IsValidDepotKey(depots[idx].DecryptionKey))
            depots[idx] = depots[idx] with { DecryptionKey = key };
    }

    private async Task<int> CopyManifestFilesAsync(
        IReadOnlyList<GitHubManifestFile> files, CancellationToken ct)
    {
        var steamPath = _locator.SteamPath!;
        var targets = new[]
        {
            Path.Combine(steamPath, "config", "depotcache"),
            Path.Combine(steamPath, "depotcache"),
        };

        foreach (var dir in targets)
            Directory.CreateDirectory(dir);

        var copied = 0;
        foreach (var mf in files)
        {
            var filename = $"{mf.DepotId}_{mf.ManifestId}.manifest";
            foreach (var dir in targets)
            {
                try { await File.WriteAllBytesAsync(Path.Combine(dir, filename), mf.Content, ct); }
                catch { /* non-fatal */ }
            }
            copied++;
        }
        return copied;
    }

    private static ManifestInstallResult Fail(string appId, string error) =>
        new(Success: false, AppId: appId, Error: error);

    private static int CountDlcLines(string lua) =>
        lua.Split('\n').Count(l => l.StartsWith("addappid(", StringComparison.Ordinal));

    private static bool IsValidDepotKey(string? key) =>
        key is { Length: 64 } && key.All(Uri.IsHexDigit);
}
