using System.Text;
using System.Text.RegularExpressions;
using OpenSteamUnlocker.Core.Manifest.Models;

namespace OpenSteamUnlocker.Core.Manifest.Lua;

/// <summary>
/// Builds SteamTools/OpenSteamTool-style Lua unlock scripts. Output format is
/// ported from cai_backend (e.g. <c>_process_steamautocracks_v2_for_steamtools</c>):
///
/// <code>
/// addappid(&lt;appId&gt;)
/// addappid(&lt;depotId&gt;, 1, "&lt;key&gt;")
/// ...
/// -- Manifests
/// setManifestid(&lt;depotId&gt;, "&lt;manifestId&gt;")   // commented out in auto-update mode
/// </code>
/// </summary>
public static class LuaManifestBuilder
{
    /// <summary>
    /// Builds the Lua script body for an app.
    /// </summary>
    /// <param name="appId">The owning game's AppID.</param>
    /// <param name="depots">Depots with keys (and optional manifest ids).</param>
    /// <param name="autoUpdate">
    /// When true (auto-update mode), <c>setManifestid</c> lines are commented
    /// out so Steam tracks the latest manifest. When false (fixed version),
    /// they pin the manifest.
    /// </param>
    public static string Build(string appId, IEnumerable<DepotInfo> depots, bool autoUpdate)
    {
        var sb = new StringBuilder();

        var depotList = depots.ToList();

        // AppID line: patch in its own key if available (mirrors Fluent-Install's patch_depot_key).
        // Some games use their AppID as a depot ID with encrypted content.
        var appEntry = depotList.Find(d => d.DepotId == appId);
        if (appEntry is not null && !string.IsNullOrEmpty(appEntry.DecryptionKey))
            sb.Append("addappid(").Append(appId).Append(", 1, \"").Append(appEntry.DecryptionKey).Append("\")\n");
        else
            sb.Append("addappid(").Append(appId).Append(")\n");

        // Write all other depots with keys (the AppID entry is already handled above).
        foreach (var depot in depotList.Where(d => d.DepotId != appId))
        {
            if (!string.IsNullOrEmpty(depot.DecryptionKey))
                sb.Append("addappid(").Append(depot.DepotId).Append(", 1, \"").Append(depot.DecryptionKey).Append("\")\n");
        }

        // Only pin manifests for depots that also have a decryption key.
        // Writing setManifestid for a keyless depot makes Steam try to decrypt an
        // unkeyed manifest, which causes the "content not decrypted" error.
        var manifestLines = depotList
            .Where(d => !string.IsNullOrEmpty(d.ManifestId) && !string.IsNullOrEmpty(d.DecryptionKey))
            .Select(d => autoUpdate
                ? $"--setManifestid({d.DepotId}, \"{d.ManifestId}\")"
                : $"setManifestid({d.DepotId}, \"{d.ManifestId}\")")
            .ToList();

        if (manifestLines.Count > 0)
        {
            sb.Append('\n');
            sb.Append(string.Join("\n", manifestLines)).Append('\n');
            sb.Append("-- Manifests\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends free-DLC <c>addappid</c> lines, skipping any already present.
    /// Mirrors <c>_add_free_dlcs_to_lua</c>.
    /// </summary>
    public static string AppendFreeDlc(string existingLua, IEnumerable<string> dlcAppIds)
    {
        // Collect all appids already present (with or without key) to avoid duplicates.
        // Matches both addappid(123) and addappid(123, 1, "key").
        var existingIds = new HashSet<string>(
            Regex.Matches(existingLua, @"addappid\((\d+)")
                 .Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);

        var sb = new StringBuilder(existingLua);
        if (existingLua.Length > 0 && !existingLua.EndsWith('\n'))
            sb.Append('\n');

        foreach (var dlc in dlcAppIds)
        {
            if (!existingIds.Contains(dlc))
            {
                sb.Append($"addappid({dlc})").Append('\n');
                existingIds.Add(dlc);
            }
        }

        return sb.ToString();
    }
}
