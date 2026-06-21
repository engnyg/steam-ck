using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using OpenSteamUnlocker.Core.Config;

namespace OpenSteamUnlocker.Core.Update;

public sealed class UpdateService : IUpdateService
{
    private readonly HttpClient    _http;
    private readonly IConfigService _config;

    public UpdateService(HttpClient http, IConfigService config)
    {
        _http   = http;
        _config = config;
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("OpenSteamUnlocker");
    }

    public Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var slug = _config.Current.UpdateRepoSlug;
        if (string.IsNullOrWhiteSpace(slug)) return null;

        try
        {
            var json = await _http.GetStringAsync(
                $"https://api.github.com/repos/{slug}/releases/latest", ct);

            using var doc  = JsonDocument.Parse(json);
            var root       = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            var tag        = tagEl.GetString();
            if (tag is null) return null;

            if (!Version.TryParse(tag.TrimStart('v'), out var remote)) return null;
            if (remote <= CurrentVersion) return null;

            var releaseUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            var notes      = root.TryGetProperty("body",     out var b) ? b.GetString()       : null;
            string? dlUrl  = null;

            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if (a.TryGetProperty("name", out var n) &&
                        n.GetString()?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true &&
                        a.TryGetProperty("browser_download_url", out var du))
                    {
                        dlUrl = du.GetString();
                        break;
                    }

            return new UpdateInfo(tag, releaseUrl, dlUrl, notes);
        }
        catch { return null; }
    }

    public async Task ApplyUpdateAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (info.DownloadUrl is null)
            throw new InvalidOperationException("無下載連結。");

        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("無法取得目前執行檔路徑。");
        var dir = Path.GetDirectoryName(currentExe)!;
        var tempPath = Path.Combine(dir, $"update_{info.Version}.exe");

        using (var stream = await _http.GetStreamAsync(info.DownloadUrl, ct))
        await using (var fs = File.Create(tempPath))
            await stream.CopyToAsync(fs, ct);

        var bat = Path.Combine(dir, "_update.bat");
        var script = $"""
            @echo off
            timeout /t 2 /nobreak >nul
            move /y "{tempPath}" "{currentExe}"
            start "" "{currentExe}"
            del "%~f0"
            """;
        await File.WriteAllTextAsync(bat, script, ct);

        Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
