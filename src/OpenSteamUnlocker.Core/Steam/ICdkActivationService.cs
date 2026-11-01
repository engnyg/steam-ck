namespace OpenSteamUnlocker.Core.Steam;

/// <summary>
/// Handles Steam CDK (CD Key) activation: validates the key format
/// and launches Steam's built-in "Activate a Product" dialog.
/// </summary>
public interface ICdkActivationService
{
    /// <summary>
    /// Returns true when <paramref name="key"/> matches a valid Steam CDK pattern
    /// (3–5 groups of 5 alphanumeric characters separated by dashes).
    /// </summary>
    bool ValidateKey(string key);

    /// <summary>
    /// Opens Steam's "Activate a Product on Steam" wizard via the
    /// <c>steam://open/activateproduct</c> URL scheme.
    /// The caller is responsible for placing the key on the clipboard
    /// before calling this method so the user can paste it.
    /// </summary>
    CdkLaunchResult Launch();
}

/// <param name="Launched">True when the Steam activation URL was dispatched successfully.</param>
/// <param name="Error">Error message when <see cref="Launched"/> is false.</param>
public sealed record CdkLaunchResult(bool Launched, string? Error);
