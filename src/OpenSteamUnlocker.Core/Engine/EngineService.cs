using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using OpenSteamUnlocker.Core.Steam;

namespace OpenSteamUnlocker.Core.Engine;

/// <summary>
/// Default <see cref="IEngineService"/>. Prefers bundled engine binaries under
/// <c>&lt;app&gt;/engine/</c>; falls back to downloading the latest
/// OpenSteamTool GitHub Release automatically.
/// </summary>
public sealed class EngineService : IEngineService
{
    private const string ReleaseApiUrl =
        "https://api.github.com/repos/OpenSteam001/OpenSteamTool/releases/latest";

    private readonly ISteamLocator _steam;
    private readonly HttpClient    _http;
    private readonly string _bundledEngineDir;
    private readonly string _backupDir;

    public EngineService(ISteamLocator steam, HttpClient http)
    {
        _steam = steam;
        _http  = http;
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

        string? steamClientHash = steamPath is not null ? ComputeSteamClientHash(steamPath) : null;
        bool patternUpToDate = steamPath is not null && steamClientHash is not null &&
                               PatternFileExists(steamPath, steamClientHash);

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
            PatternUpToDate = patternUpToDate,
            SteamClientHash = steamClientHash,
        };
    }

    public async Task InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steamPath = RequireSteamPath();
        Directory.CreateDirectory(_backupDir);

        progress?.Report("正在初始化…");

        foreach (var proc in Process.GetProcessesByName("steam"))
        {
            try { proc.Kill(); } catch { }
        }
        await Task.Delay(2000, ct).ConfigureAwait(false);

        if (BinariesAreBundled())
        {
            var dlls = EngineFiles.All.ToDictionary(
                dll => dll,
                dll => File.ReadAllBytes(Path.Combine(_bundledEngineDir, dll)));
            await InstallDllsAsync(dlls, steamPath, ct).ConfigureAwait(false);
        }
        else
        {
            var zipBytes = await DownloadReleaseZipAsync(null, ct).ConfigureAwait(false);
            var dlls = ExtractDllsFromZip(zipBytes);
            await InstallDllsAsync(dlls, steamPath, ct).ConfigureAwait(false);
            await InstallPatternFilesFromZipAsync(zipBytes, steamPath, ct).ConfigureAwait(false);
        }

        await EnsureTomlAsync(ct).ConfigureAwait(false);
        progress?.Report("初始化完成！請重新啟動 Steam。");
    }

    private async Task InstallDllsAsync(Dictionary<string, byte[]> dlls, string steamPath, CancellationToken ct)
    {
        foreach (var (dll, bytes) in dlls)
        {
            var target = Path.Combine(steamPath, dll);
            if (File.Exists(target))
            {
                var backup = Path.Combine(_backupDir, dll);
                if (!File.Exists(backup))
                    File.Copy(target, backup, overwrite: false);
            }
            await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> DownloadReleaseZipAsync(IProgress<string>? progress, CancellationToken ct)
    {
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "OpenSteamUnlocker");
        var metaJson = await _http.GetStringAsync(ReleaseApiUrl, ct).ConfigureAwait(false);

        string? zipUrl = null;
        using (var doc = JsonDocument.Parse(metaJson))
        {
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("Release", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }

        if (zipUrl is null)
            throw new InvalidOperationException("找不到 Release 下載連結。");

        progress?.Report("正在下載…");
        return await _http.GetByteArrayAsync(zipUrl, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, byte[]> ExtractDllsFromZip(byte[] zipBytes)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var ms      = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            if (!EngineFiles.All.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;
            using var es  = entry.Open();
            using var buf = new MemoryStream();
            es.CopyTo(buf);
            result[entry.Name] = buf.ToArray();
        }

        var missing = EngineFiles.All.Where(dll => !result.ContainsKey(dll)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Release 壓縮檔中缺少以下 DLL：{string.Join(", ", missing)}");

        return result;
    }

    private static async Task InstallPatternFilesFromZipAsync(
        byte[] zipBytes, string steamPath, CancellationToken ct)
    {
        var patternDir = Path.Combine(steamPath, "opensteamtool", "pattern", "steamclient");
        Directory.CreateDirectory(patternDir);

        using var ms      = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            if (!entry.Name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)) continue;
            var entryPath = entry.FullName.Replace('\\', '/');
            if (!entryPath.Contains("pattern/steamclient/", StringComparison.OrdinalIgnoreCase)) continue;

            using var es  = entry.Open();
            using var buf = new MemoryStream();
            await es.CopyToAsync(buf, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(patternDir, entry.Name), buf.ToArray(), ct).ConfigureAwait(false);
        }
    }

    public async Task UpdatePatternsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steamPath = RequireSteamPath();
        progress?.Report("正在從 GitHub 取得最新 Release…");
        var zipBytes = await DownloadReleaseZipAsync(progress, ct).ConfigureAwait(false);
        progress?.Report("正在安裝 Pattern 檔…");
        await InstallPatternFilesFromZipAsync(zipBytes, steamPath, ct).ConfigureAwait(false);
        progress?.Report("Pattern 檔更新完成！請重新啟動 Steam。");
    }

    public async Task UninstallAsync(CancellationToken ct = default)
    {
        var steamPath = RequireSteamPath();

        foreach (var proc in Process.GetProcessesByName("steam"))
        {
            try { proc.Kill(); } catch { }
        }
        await Task.Delay(2000, ct).ConfigureAwait(false);

        foreach (var dll in EngineFiles.All)
        {
            var target = Path.Combine(steamPath, dll);
            var backup = Path.Combine(_backupDir, dll);

            TryDelete(target);

            if (File.Exists(backup))
            {
                try { File.Copy(backup, target, overwrite: true); } catch { }
                TryDelete(backup);
            }
        }
    }

    public async Task WriteLuaAsync(string appId, string luaContent, CancellationToken ct = default)
    {
        var luaDir = RequireLuaDir();
        Directory.CreateDirectory(luaDir);
        await File.WriteAllTextAsync(Path.Combine(luaDir, $"{appId}.lua"), luaContent, ct).ConfigureAwait(false);
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

    private static string? ComputeSteamClientHash(string steamPath)
    {
        var dll = Path.Combine(steamPath, "steamclient.dll");
        if (!File.Exists(dll)) return null;
        try
        {
            using var fs = File.OpenRead(dll);
            var hash = SHA256.HashData(fs);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch { return null; }
    }

    private static bool PatternFileExists(string steamPath, string hash)
    {
        var path = Path.Combine(steamPath, "opensteamtool", "pattern", "steamclient", $"{hash}.toml");
        return File.Exists(path);
    }

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
        # opensteamtool.toml
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
