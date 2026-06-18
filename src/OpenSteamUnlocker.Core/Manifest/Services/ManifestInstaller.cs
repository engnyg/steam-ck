using OpenSteamUnlocker.Core.Engine;
using OpenSteamUnlocker.Core.Manifest.Lua;
using OpenSteamUnlocker.Core.Manifest.Models;
using OpenSteamUnlocker.Core.Manifest.Providers;
using OpenSteamUnlocker.Core.Steam;

namespace OpenSteamUnlocker.Core.Manifest.Services;

/// <summary>
/// Orchestrates the full manifest install pipeline.
///
/// Priority:
///   1. GitHub repos — key.vdf depot keys + *.manifest binary files.
///   2. Sudama API  — depot keys from the Sudama database, manifest IDs from
///      CaiGames/SteamCMD (no binary manifests required).
///
/// In both cases the result is an OpenSteamTool-compatible Lua script written
/// to <c>&lt;Steam&gt;/config/lua/{appId}.lua</c>, plus optional depot-cache
/// manifest copies and free-DLC addappid lines.
/// </summary>
public sealed class ManifestInstaller : IManifestInstaller
{
    // First repo tried for every install; custom repos come before this one.
    private const string DefaultRepo = "SteamAutoCracks/ManifestHub";

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
            return Fail(appId, "AppID cannot be empty.");

        if (!_locator.IsInstalled)
            return Fail(appId, "Steam installation not found.");

        // Build the ordered list of GitHub repos to try.
        var repos = new List<string>();
        if (options.ExtraGitHubRepos is { Count: > 0 } extra)
            repos.AddRange(extra);
        repos.Add(DefaultRepo);

        // ── 1. Try GitHub repos ───────────────────────────────────────────────
        foreach (var repo in repos)
        {
            progress?.Report($"Checking {repo}…");
            var data = await _github.FetchAsync(
                appId, repo, options.GithubPersonalToken,
                skipManifestFiles: options.AutoUpdate, ct);

            if (data is null) continue;

            progress?.Report("Writing Lua script…");
            var depots = data.DepotKeys
                .Select(kv =>
                {
                    // Try to pair with a manifest from the downloaded files
                    var mf = data.ManifestFiles.FirstOrDefault(f => f.DepotId == kv.Key);
                    return new DepotInfo(kv.Key, kv.Value, mf?.ManifestId);
                })
                .ToList();

            // Depots with no key but with a manifest (rare — include manifest ID only)
            foreach (var mf in data.ManifestFiles)
            {
                if (!data.DepotKeys.ContainsKey(mf.DepotId))
                    depots.Add(new DepotInfo(mf.DepotId, null, mf.ManifestId));
            }

            var lua = LuaManifestBuilder.Build(appId, depots, options.AutoUpdate);

            if (options.AddFreeDlc)
            {
                progress?.Report("Fetching DLC list…");
                var dlcIds = await _steamData.GetDlcIdsAsync(appId, ct);
                if (dlcIds.Count > 0)
                    lua = LuaManifestBuilder.AppendFreeDlc(lua, dlcIds);
            }

            await _engine.WriteLuaAsync(appId, lua, ct);

            int copied = 0;
            if (!options.AutoUpdate && data.ManifestFiles.Count > 0)
            {
                progress?.Report("Copying manifest files…");
                copied = await CopyManifestFilesAsync(data.ManifestFiles, ct);
            }

            var luaPath = Path.Combine(_locator.LuaConfigPath!, $"{appId}.lua");
            progress?.Report("Done.");
            return new ManifestInstallResult(
                Success: true, AppId: appId,
                LuaPath: luaPath, ManifestsCopied: copied,
                DlcsAdded: CountDlcLines(lua), ProviderUsed: repo);
        }

        // ── 2. Sudama API fallback ─────────────────────────────────────────────
        progress?.Report("Fetching depot data from API…");
        var apiDepots = await _steamData.GetDepotsAsync(appId, ct);
        if (apiDepots.Count == 0)
            return Fail(appId, "No depot data found in any source.");

        progress?.Report("Loading depot keys…");
        var allKeys = await _depotKeys.GetAllKeysAsync(ct);

        var matchedDepots = apiDepots
            .Select(d => new DepotInfo(
                d.DepotId,
                allKeys.TryGetValue(d.DepotId, out var key) ? key : null,
                d.ManifestId))
            .ToList();

        var apiLua = LuaManifestBuilder.Build(appId, matchedDepots, options.AutoUpdate);

        if (options.AddFreeDlc)
        {
            progress?.Report("Fetching DLC list…");
            var dlcIds = await _steamData.GetDlcIdsAsync(appId, ct);
            if (dlcIds.Count > 0)
                apiLua = LuaManifestBuilder.AppendFreeDlc(apiLua, dlcIds);
        }

        await _engine.WriteLuaAsync(appId, apiLua, ct);
        progress?.Report("Done.");

        return new ManifestInstallResult(
            Success: true, AppId: appId,
            LuaPath: Path.Combine(_locator.LuaConfigPath!, $"{appId}.lua"),
            ManifestsCopied: 0, DlcsAdded: CountDlcLines(apiLua),
            ProviderUsed: "Sudama API");
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
}
