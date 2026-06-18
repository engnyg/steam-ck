namespace OpenSteamUnlocker.Core.Manifest.Services;

/// <summary>
/// Provides Steam depot decryption keys sourced from the Sudama key database
/// (<c>api.993499094.xyz/depotkeys.json</c>), cached locally for 24 hours.
/// </summary>
public interface IDepotKeyService
{
    /// <summary>
    /// Returns the full depot → key dictionary, downloading and caching on first call.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetAllKeysAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the decryption key for a single depot, or null if not found.
    /// </summary>
    Task<string?> GetKeyAsync(string depotId, CancellationToken ct = default);
}
