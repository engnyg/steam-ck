namespace OpenSteamUnlocker.Core.Http;

/// <summary>
/// Detects whether the current network egress is inside mainland China, which
/// decides whether GitHub mirror proxies are used. Ported from <c>checkcn()</c>.
/// </summary>
public interface IRegionDetector
{
    /// <summary>
    /// True when the region is (or is assumed to be) mainland China. Defaults
    /// to true until <see cref="DetectAsync"/> determines otherwise — matching
    /// the Python behaviour of failing safe to "use mirrors".
    /// </summary>
    bool IsChina { get; }

    Task<bool> DetectAsync(CancellationToken ct = default);
}
