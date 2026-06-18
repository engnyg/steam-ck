using OpenSteamUnlocker.Core.Manifest.Models;

namespace OpenSteamUnlocker.Core.Manifest.Services;

/// <summary>
/// Orchestrates the full manifest install pipeline:
/// GitHub repo download → Sudama API fallback → Lua write → depotcache copy.
/// </summary>
public interface IManifestInstaller
{
    /// <summary>
    /// Installs manifests for <paramref name="appId"/> using the given options.
    /// Progress messages are reported to <paramref name="progress"/> when provided.
    /// </summary>
    Task<ManifestInstallResult> InstallAsync(
        string appId,
        ManifestInstallOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}
