namespace OpenSteamUnlocker.Core.Manifest.Models;

/// <summary>
/// Controls which manifest source(s) the installer uses to obtain depot keys
/// and manifest data. Mirrors the "清單來源" selector in Fluent-Install.
/// </summary>
public enum ManifestSourceMode
{
    /// <summary>
    /// Tries sources in order until one provides at least one valid depot key:
    /// SudamaGitHub → Cysaw → SteamAutoCracks.
    /// </summary>
    Auto,

    /// <summary>
    /// Sudama key database + Auiowu/Satisl/etc. GitHub repo search.
    /// Gets manifest IDs from CaiGames/SteamCMD, supplemented by manifest.steam.run.
    /// Keys come from all three Sudama-family databases plus per-AppID Key.vdf files.
    /// </summary>
    SudamaGitHub,

    /// <summary>
    /// Cysaw POST API (cysaw.pw/proxy). Downloads a ZIP containing
    /// *.manifest binary files and *.lua (or key.vdf) with depot keys.
    /// </summary>
    Cysaw,

    /// <summary>
    /// SteamAutoCracks/ManifestHub per-AppID branch via GitHub API
    /// (falls back to codeload.github.com ZIP on rate-limit / API exhaustion).
    /// </summary>
    SteamAutoCracks,
}
