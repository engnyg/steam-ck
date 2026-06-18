using OpenSteamUnlocker.Core.Steam;

namespace OpenSteamUnlocker.Core.Engine;

/// <summary>
/// Default <see cref="IEngineService"/>. Bundled engine binaries are expected
/// under <c>&lt;app&gt;/engine/</c> (populated by the OpenSteamTool build).
/// </summary>
public sealed class EngineService : IEngineService
{
    private readonly ISteamLocator _steam;
    private readonly string _bundledEngineDir;
    private readonly string _backupDir;

    public EngineService(ISteamLocator steam)
    {
        _steam = steam;
        _bundledEngineDir = Path.Combine(AppContext.BaseDirectory, "engine");
        _backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSteamUnlocker", "backup", "engine");
    }

    public EngineStatus GetStatus()
    {
        var steamPath = _steam.SteamPath;
        var luaDir = _steam.LuaConfigPath;

        var present = new List<string>();
        var missing = new List<string>();
        if (steamPath is not null)
        {
            foreach (var dll in EngineFiles.All)
            {
                if (File.Exists(Path.Combine(steamPath, dll)))
                    present.Add(dll);
                else
                    missing.Add(dll);
            }
        }

        return new EngineStatus
        {
            SteamFound = _steam.IsInstalled,
            SteamPath = steamPath,
            Installed = steamPath is not null && missing.Count == 0,
            PresentFiles = present,
            MissingFiles = missing,
            BinariesBundled = BinariesAreBundled(),
            LuaDirectory = luaDir,
            InstalledApps = ListInstalledApps(luaDir),
        };
    }

    public async Task InstallAsync(CancellationToken ct = default)
    {
        var steamPath = RequireSteamPath();

        if (!BinariesAreBundled())
            throw new InvalidOperationException(
                $"找不到引擎二進位檔。請先建置 OpenSteamTool 並將 DLL 放入 '{_bundledEngineDir}'。");

        Directory.CreateDirectory(_backupDir);

        foreach (var dll in EngineFiles.All)
        {
            var source = Path.Combine(_bundledEngineDir, dll);
            var target = Path.Combine(steamPath, dll);

            // Back up a pre-existing (non-engine) file once, so uninstall can restore it.
            if (File.Exists(target))
            {
                var backup = Path.Combine(_backupDir, dll);
                if (!File.Exists(backup))
                    File.Copy(target, backup, overwrite: false);
            }

            await CopyFileAsync(source, target, ct).ConfigureAwait(false);
        }

        await EnsureTomlAsync(ct).ConfigureAwait(false);
    }

    public Task UninstallAsync(CancellationToken ct = default)
    {
        var steamPath = RequireSteamPath();

        foreach (var dll in EngineFiles.All)
        {
            var target = Path.Combine(steamPath, dll);
            var backup = Path.Combine(_backupDir, dll);

            TryDelete(target);

            // Restore a previously backed-up original, if any.
            if (File.Exists(backup))
            {
                File.Copy(backup, target, overwrite: true);
                TryDelete(backup);
            }
        }

        return Task.CompletedTask;
    }

    public async Task WriteLuaAsync(string appId, string luaContent, CancellationToken ct = default)
    {
        var dir = RequireLuaDir();
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{appId}.lua"), luaContent, ct).ConfigureAwait(false);
    }

    public Task RemoveLuaAsync(string appId, CancellationToken ct = default)
    {
        var dir = RequireLuaDir();
        TryDelete(Path.Combine(dir, $"{appId}.lua"));
        return Task.CompletedTask;
    }

    public async Task<string?> ReadLuaAsync(string appId, CancellationToken ct = default)
    {
        var dir = _steam.LuaConfigPath;
        if (dir is null)
            return null;
        var path = Path.Combine(dir, $"{appId}.lua");
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)
            : null;
    }

    public async Task EnsureTomlAsync(CancellationToken ct = default)
    {
        var steamPath = RequireSteamPath();
        var tomlPath = Path.Combine(steamPath, "opensteamtool.toml");
        if (!File.Exists(tomlPath))
            await File.WriteAllTextAsync(tomlPath, DefaultToml, ct).ConfigureAwait(false);
    }

    // --- helpers -----------------------------------------------------------

    private bool BinariesAreBundled() =>
        Directory.Exists(_bundledEngineDir) &&
        EngineFiles.All.All(dll => File.Exists(Path.Combine(_bundledEngineDir, dll)));

    private static IReadOnlyList<string> ListInstalledApps(string? luaDir)
    {
        if (luaDir is null || !Directory.Exists(luaDir))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(luaDir, "*.lua")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name)
            .ToList();
    }

    private string RequireSteamPath() =>
        _steam.SteamPath ?? throw new InvalidOperationException("未偵測到 Steam 安裝。");

    private string RequireLuaDir() =>
        _steam.LuaConfigPath ?? throw new InvalidOperationException("未偵測到 Steam 安裝。");

    private static async Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        await using var src = File.OpenRead(source);
        await using var dst = File.Create(target);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private const string DefaultToml = """
        # opensteamtool.toml — generated by OpenSteam Unlocker
        # Place at: <Steam>/opensteamtool.toml

        [log]
        level = "info"

        [manifest]
        # "opensteamtool" (default) | "wudrm" (China) | "steamrun"
        url = "opensteamtool"
        timeout_resolve_ms = 5000
        timeout_connect_ms = 5000
        timeout_send_ms    = 10000
        timeout_recv_ms    = 10000

        [lua]
        # Default <Steam>/config/lua is always loaded last.

        [inject]
        enabled = false

        """;
}
