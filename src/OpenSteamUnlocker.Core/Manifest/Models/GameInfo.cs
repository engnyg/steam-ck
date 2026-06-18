namespace OpenSteamUnlocker.Core.Manifest.Models;

/// <summary>
/// Lightweight game summary returned by search and info lookups.
/// </summary>
public sealed record GameInfo(
    string AppId,
    string Name,
    string? HeaderImageUrl = null,
    string? Description = null);
