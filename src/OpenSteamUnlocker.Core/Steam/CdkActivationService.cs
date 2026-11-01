using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OpenSteamUnlocker.Core.Steam;

/// <summary>
/// Validates Steam CDK keys and launches Steam's built-in activation wizard.
/// </summary>
public sealed class CdkActivationService : ICdkActivationService
{
    // Steam CDK format: 3–5 groups of exactly 5 uppercase alphanumeric chars.
    // Examples: XXXXX-XXXXX-XXXXX  /  XXXXX-XXXXX-XXXXX-XXXXX-XXXXX
    private static readonly Regex _keyPattern =
        new(@"^[A-Z0-9]{5}(-[A-Z0-9]{5}){2,4}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <inheritdoc />
    public bool ValidateKey(string key) =>
        !string.IsNullOrWhiteSpace(key) &&
        _keyPattern.IsMatch(key.Trim());

    /// <inheritdoc />
    public CdkLaunchResult Launch()
    {
        try
        {
            // steam://open/activateproduct  opens Steam's "Activate a Product"
            // wizard without requiring us to pass the key on the command line.
            // The caller is expected to have placed the key on the clipboard
            // so the user can paste it with Ctrl+V.
            Process.Start(new ProcessStartInfo("steam://open/activateproduct")
            {
                UseShellExecute = true
            });

            return new CdkLaunchResult(true, null);
        }
        catch (Exception ex)
        {
            return new CdkLaunchResult(false, ex.Message);
        }
    }
}
