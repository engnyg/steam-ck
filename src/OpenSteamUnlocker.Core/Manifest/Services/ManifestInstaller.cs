using OpenSteamUnlocker.Core.Engine;
using OpenSteamUnlocker.Core.Manifest.Lua;
using OpenSteamUnlocker.Core.Manifest.Models;
using OpenSteamUnlocker.Core.Manifest.Providers;
using OpenSteamUnlocker.Core.Steam;

namespace OpenSteamUnlocker.Core.Manifest.Services;

/// <summary>
/// Orchestrates the full manifest install pipeline.
///
/// Flow (Sudama-first):
///   0. Pre-fetch depot key database (Sudama + ManifestHub) — always, eagerly.
///   1. Fetch depot/manifest-ID data from CaiGames/SteamCMD API.
///   2. Match depot IDs to keys from step 0.
///   3. Try GitHub repos to supplement: fill remaining key gaps from key.vdf,
///      update manifest IDs, and download binary *.manifest files.
///   4. Write Lua script; copy manifest files to depotcache.
///
/// Sudama keys are pre-fetched once and reused across all sub-steps, so
/// ManifestAutoUpdate repos (which carry only .manifest, no key.vdf) still
/// receive correct decryption keys from the database.
/// </summary>
public sealed class ManifestInstaller : IManifestInstaller
{
    // Repos with per-AppID branches (branch name = appId) containing Key.vdf + *.manifest.
    // SteamAutoCracks/ManifestHub and SSMGAlt/ManifestHub2 are excluded — they only have
    // a global main branch with depotkeys.json (covered by DepotKeyService / Sudama).
    private static readonly IReadOnlyList<string> DefaultRepos =
    [
        "Auiowu/ManifestAutoUpdate",
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

    public ManifestInstaller(
        IGitHubManifestProvider github,
        ISteamDataService       steamData,
        IDepotKeyService        depotKeys,
        IEngineService          engine,
        ISteamLocator           locator)
    {
        _github    = github;
        _steamData = steamData;
        _depotKeys = depotKeys;
        _engine    = engine;
        _locator   = locator;
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

        // ── Step 0: Pre-fetch depot key database (Sudama-first) ───────────────
        // Done eagerly so every subsequent step can use the same in-memory cache.
        progress?.Report("正在載入 Sudama 解密金鑰庫…");
        var allDepotKeys = await _depotKeys.GetAllKeysAsync(ct);
        progress?.Report($"金鑰庫載入完成，共 {allDepotKeys.Count:N0} 個金鑰。");

        // ── Step 1: Fetch depot/manifest-ID list from CaiGames/SteamCMD ───────
        progress?.Report("正在從 API 取得遊戲 depot 清單…");
        var apiDepots = await _steamData.GetDepotsAsync(appId, ct);

        // Build the initial depot list using API manifest IDs + Sudama keys.
        var depots = apiDepots
            .Select(d => new DepotInfo(
                d.DepotId,
                allDepotKeys.TryGetValue(d.DepotId, out var k) && IsValidDepotKey(k) ? k : null,
                d.ManifestId))
            .ToList();

        var sudamaKeyCount = depots.Count(d => IsValidDepotKey(d.DecryptionKey));
        progress?.Report($"API 找到 {depots.Count} 個 depot，Sudama 提供 {sudamaKeyCount} 個解密金鑰。");

        // ── Step 2: Try GitHub repos — supplement keys + get manifest files ────
        var repos = new List<string>();
        if (options.ExtraGitHubRepos is { Count: > 0 } extra)
            repos.AddRange(extra);
        repos.AddRange(DefaultRepos);

        List<GitHubManifestFile> manifestFiles = [];
        string? gitHubRepo = null;

        foreach (var repo in repos)
        {
            progress?.Report($"正在從 {repo} 取得 manifest 資料…");
            var data = await _github.FetchAsync(
                appId, repo, options.GithubPersonalToken,
                skipManifestFiles: options.AutoUpdate, ct);

            if (data is null) continue;

            // Merge key.vdf keys: fill any depot still missing a valid key.
            foreach (var (depotId, vdfKey) in data.DepotKeys)
            {
                if (!IsValidDepotKey(vdfKey)) continue;
                var idx = depots.FindIndex(d => d.DepotId == depotId);
                if (idx >= 0 && !IsValidDepotKey(depots[idx].DecryptionKey))
                    depots[idx] = depots[idx] with { DecryptionKey = vdfKey };
                else if (idx < 0)
                    depots.Add(new DepotInfo(depotId, vdfKey, null));
            }

            // Merge manifest files: update manifest IDs and collect binary files.
            foreach (var mf in data.ManifestFiles)
            {
                var idx = depots.FindIndex(d => d.DepotId == mf.DepotId);
                if (idx >= 0)
                    depots[idx] = depots[idx] with { ManifestId = mf.ManifestId };
                else
                    depots.Add(new DepotInfo(mf.DepotId,
                        allDepotKeys.TryGetValue(mf.DepotId, out var mk) && IsValidDepotKey(mk) ? mk : null,
                        mf.ManifestId));
            }

            if (data.ManifestFiles.Count > 0)
            {
                manifestFiles = [.. data.ManifestFiles];
                gitHubRepo    = repo;
                break; // first repo with actual files is sufficient
            }
        }

        if (depots.Count == 0)
            return Fail(appId, "所有來源均找不到此遊戲的 depot 資料。");

        // ── Step 3: Write Lua + copy manifests ────────────────────────────────
        var totalKeys = depots.Count(d => IsValidDepotKey(d.DecryptionKey));
        progress?.Report($"共 {totalKeys}/{depots.Count} 個 depot 有解密金鑰，正在寫入 Lua…");

        var lua = LuaManifestBuilder.Build(appId, depots, options.AutoUpdate);

        if (options.AddFreeDlc)
        {
            progress?.Report("正在取得 DLC 清單…");
            var dlcIds = await _steamData.GetDlcIdsAsync(appId, ct);
            if (dlcIds.Count > 0)
                lua = LuaManifestBuilder.AppendFreeDlc(lua, dlcIds);
        }

        await _engine.WriteLuaAsync(appId, lua, ct);

        int copied = 0;
        if (!options.AutoUpdate && manifestFiles.Count > 0)
        {
            progress?.Report("正在複製 manifest 檔案到 depotcache…");
            copied = await CopyManifestFilesAsync(manifestFiles, ct);
        }

        var provider = gitHubRepo is not null
            ? $"Sudama + {gitHubRepo}"
            : "Sudama API";

        progress?.Report("完成。");
        return new ManifestInstallResult(
            Success: true, AppId: appId,
            LuaPath: Path.Combine(_locator.LuaConfigPath!, $"{appId}.lua"),
            ManifestsCopied: copied,
            DlcsAdded: CountDlcLines(lua),
            ProviderUsed: provider);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<int> CopyManifestFilesAsync(
        IReadOnlyList<GitHubManifestFile> files, CancellationToken ct)
    {
        var steamPath = _locator.SteamPath!;
        // Steam checks both locations; copy to both.
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
                try
                {
                    await File.WriteAllBytesAsync(Path.Combine(dir, filename), mf.Content, ct);
                }
                catch { /* non-fatal — continue with remaining files */ }
            }
            copied++;
        }
        return copied;
    }

    private static ManifestInstallResult Fail(string appId, string error) =>
        new(Success: false, AppId: appId, Error: error);

    private static int CountDlcLines(string lua) =>
        lua.Split('\n').Count(l => l.StartsWith("addappid(", StringComparison.Ordinal));

    // Steam depot keys are 32-byte AES keys encoded as 64 hex characters.
    // OpenSteamTool silently drops keys that are not exactly 64 chars.
    private static bool IsValidDepotKey(string? key) =>
        key is { Length: 64 } && key.All(Uri.IsHexDigit);
}
