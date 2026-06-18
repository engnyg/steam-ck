namespace OpenSteamUnlocker.Core.Manifest.Models;

/// <summary>
/// Raw depot + manifest data as returned by CaiGames / SteamCMD APIs.
/// Does not include the decryption key (that comes from <see cref="IDepotKeyService"/>).
/// </summary>
public sealed record SteamDepotData(
    string DepotId,
    string ManifestId,
    long DownloadSize = 0,
    string? DlcAppId = null);
