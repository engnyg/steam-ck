namespace OpenSteamUnlocker.Core.Manifest.Models;

/// <summary>
/// Parameters for a manifest install operation.
/// </summary>
public sealed record ManifestInstallOptions(
    /// <summary>
    /// When true, <c>setManifestid</c> lines are commented out so Steam tracks
    /// the latest manifest automatically.
    /// </summary>
    bool AutoUpdate = false,

    /// <summary>Append free DLC <c>addappid</c> lines to the Lua script.</summary>
    bool AddFreeDlc = false,

    /// <summary>
    /// GitHub Personal Access Token (5000 req/hr vs 60 unauthenticated).
    /// </summary>
    string? GithubPersonalToken = null,

    /// <summary>
    /// Extra GitHub repos to probe in addition to <c>SteamAutoCracks/ManifestHub</c>.
    /// Format: <c>"owner/repo"</c>.
    /// </summary>
    IReadOnlyList<string>? ExtraGitHubRepos = null);
