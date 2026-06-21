namespace OpenSteamUnlocker.Core.Update;

public sealed record UpdateInfo(
    string Version,
    string ReleaseUrl,
    string? DownloadUrl,
    string? ReleaseNotes);

/// <summary>
/// Checks whether a newer release is available on GitHub.
/// Returns null when no update exists or the check is disabled.
/// </summary>
public interface IUpdateService
{
    Version CurrentVersion { get; }
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);
    Task ApplyUpdateAsync(UpdateInfo info, CancellationToken ct = default);
}
