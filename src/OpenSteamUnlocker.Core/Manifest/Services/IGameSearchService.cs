using OpenSteamUnlocker.Core.Manifest.Models;

namespace OpenSteamUnlocker.Core.Manifest.Services;

/// <summary>
/// Game search and info lookup backed by CaiGames API with Steam Store fallback.
/// </summary>
public interface IGameSearchService
{
    /// <summary>
    /// Searches for games by name. Returns up to 20 results.
    /// </summary>
    Task<IReadOnlyList<GameInfo>> SearchAsync(string term, CancellationToken ct = default);

    /// <summary>
    /// Returns the AppID for an exact URL or numeric ID input, or null if not parseable.
    /// Handles full Steam store URLs, SteamDB URLs and bare numeric IDs.
    /// </summary>
    string? ExtractAppId(string userInput);
}
