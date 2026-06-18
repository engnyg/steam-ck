namespace OpenSteamUnlocker.Core.Manifest.Models;

/// <summary>
/// A Steam depot together with its decryption key and (optional) pinned
/// manifest id.
/// </summary>
public sealed record DepotInfo(string DepotId, string? DecryptionKey = null, string? ManifestId = null);

/// <summary>The unlocker backend a Lua/AppList payload targets.</summary>
public enum UnlockerType
{
    /// <summary>SteamTools / OpenSteamTool (Lua-based).</summary>
    SteamTools,

    /// <summary>GreenLuma (AppList-based).</summary>
    GreenLuma,
}
