using OpenSteamUnlocker.Core.Manifest.Models;

namespace OpenSteamUnlocker.Core.Manifest.Services;

/// <summary>
/// Queries CaiGames / SteamCMD / Steam Store APIs for depot and DLC metadata.
/// </summary>
public interface ISteamDataService
{
    /// <summary>
    /// Returns the list of depots (with their public manifest IDs) for an app.
    /// Tries CaiGames → SteamCMD → Steam Store in sequence.
    /// </summary>
    Task<IReadOnlyList<SteamDepotData>> GetDepotsAsync(string appId, CancellationToken ct = default);

    /// <summary>
    /// Returns the DLC AppIDs for a base game.
    /// Tries CaiGames → SteamCMD → Steam Store in sequence.
    /// </summary>
    Task<IReadOnlyList<string>> GetDlcIdsAsync(string appId, CancellationToken ct = default);

    /// <summary>
    /// Returns basic game info (name, header image). Returns a minimal record
    /// on failure rather than throwing.
    /// </summary>
    Task<GameInfo> GetGameInfoAsync(string appId, CancellationToken ct = default);

    /// <summary>
    /// Downloads raw image bytes from a URL. Returns null on failure.
    /// </summary>
    Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct = default);
}
