namespace OpenSteamUnlocker.Core.Manifest.Providers;

/// <summary>
/// Data downloaded from a GitHub branch-per-appid manifest repository
/// (e.g. <c>SteamAutoCracks/ManifestHub</c>).
/// </summary>
public sealed record GitHubManifestData(
    /// <summary>
    /// Depot decryption keys parsed from <c>key.vdf</c>.
    /// Key = depotId, Value = hex decryption key.
    /// </summary>
    IReadOnlyDictionary<string, string> DepotKeys,

    /// <summary>
    /// The <c>*.manifest</c> binary files found in the branch.
    /// </summary>
    IReadOnlyList<GitHubManifestFile> ManifestFiles);

/// <summary>A single <c>{depotId}_{manifestId}.manifest</c> file.</summary>
public sealed record GitHubManifestFile(
    string DepotId,
    string ManifestId,
    byte[] Content);

/// <summary>
/// Downloads manifest data from a GitHub repository whose branches are named
/// by Steam AppID (the <c>SteamAutoCracks/ManifestHub</c> convention).
/// </summary>
public interface IGitHubManifestProvider
{
    /// <summary>
    /// Fetches manifest data for <paramref name="appId"/> from the given repo.
    /// Returns null when the branch does not exist or all downloads fail.
    /// </summary>
    /// <param name="appId">Steam AppID (= branch name).</param>
    /// <param name="repo">GitHub repo in <c>owner/name</c> format.</param>
    /// <param name="githubToken">Optional PAT for higher API rate limits.</param>
    /// <param name="skipManifestFiles">
    /// When true (auto-update mode), <c>.manifest</c> binaries are not downloaded.
    /// </param>
    Task<GitHubManifestData?> FetchAsync(
        string appId,
        string repo,
        string? githubToken     = null,
        bool   skipManifestFiles = false,
        CancellationToken ct    = default);
}
