namespace OpenSteamUnlocker.Core.Config;

/// <summary>
/// Loads and persists <see cref="AppConfig"/>. Mirrors cai_backend's
/// gen_config_file / load_config behaviour (auto-generate on first run,
/// reset on corruption).
/// </summary>
public interface IConfigService
{
    /// <summary>The currently loaded configuration.</summary>
    AppConfig Current { get; }

    /// <summary>Loads config.json, creating defaults if missing/corrupt.</summary>
    Task<AppConfig> LoadAsync(CancellationToken ct = default);

    /// <summary>Persists <see cref="Current"/> to config.json.</summary>
    Task SaveAsync(CancellationToken ct = default);
}
