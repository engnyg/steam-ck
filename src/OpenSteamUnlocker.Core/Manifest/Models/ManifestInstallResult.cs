namespace OpenSteamUnlocker.Core.Manifest.Models;

/// <summary>
/// Outcome of a <see cref="IManifestInstaller.InstallAsync"/> call.
/// </summary>
public sealed record ManifestInstallResult(
    bool Success,
    string AppId,
    string? LuaPath = null,
    int ManifestsCopied = 0,
    int DlcsAdded = 0,
    string? Error = null,
    string? ProviderUsed = null);
