using System.Text.Json;

namespace OpenSteamUnlocker.Core.Config;

/// <summary>
/// File-backed <see cref="IConfigService"/>. Stores config under
/// <c>%LOCALAPPDATA%\OpenSteamUnlocker\config\config.json</c>.
/// </summary>
public sealed class ConfigService : IConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _configPath;
    private AppConfig _current = new();

    public ConfigService()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSteamUnlocker");
        _configPath = Path.Combine(root, "config", "config.json");
    }

    public AppConfig Current => _current;

    public async Task<AppConfig> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_configPath))
        {
            _current = new AppConfig();
            await SaveAsync(ct).ConfigureAwait(false);
            return _current;
        }

        try
        {
            await using var stream = File.OpenRead(_configPath);
            var loaded = await JsonSerializer.DeserializeAsync<AppConfig>(stream, SerializerOptions, ct)
                         .ConfigureAwait(false);
            _current = loaded ?? new AppConfig();
            _current.CustomRepos ??= new CustomRepos();
        }
        catch
        {
            // Corrupt config: reset to defaults (parity with load_config()).
            TryDelete(_configPath);
            _current = new AppConfig();
            await SaveAsync(ct).ConfigureAwait(false);
        }

        return _current;
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        await using var stream = File.Create(_configPath);
        await JsonSerializer.SerializeAsync(stream, _current, SerializerOptions, ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
