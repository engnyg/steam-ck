using System.Text;
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
        sb.Append("addappid(").Append(appId).Append(")\n");

        var depotList = depots.ToList();

        foreach (var depot in depotList)
        {
            if (!string.IsNullOrEmpty(depot.DecryptionKey))
                sb.Append("addappid(").Append(depot.DepotId).Append(", 1, \"").Append(depot.DecryptionKey).Append("\")\n");
        }

        var manifestLines = depotList
            .Where(d => !string.IsNullOrEmpty(d.ManifestId))
            .Select(d => autoUpdate
                ? $"--setManifestid({d.DepotId}, \"{d.ManifestId}\")"
                : $"setManifestid({d.DepotId}, \"{d.ManifestId}\")")
            .ToList();

        if (manifestLines.Count > 0)
        {
            sb.Append("\n-- Manifests\n");
            sb.Append(string.Join("\n", manifestLines)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends free-DLC <c>addappid</c> lines, skipping any already present.
    /// Mirrors <c>_add_free_dlcs_to_lua</c>.
    /// </summary>
    public static string AppendFreeDlc(string existingLua, IEnumerable<string> dlcAppIds)
    {
        var sb = new StringBuilder(existingLua);
        if (existingLua.Length > 0 && !existingLua.EndsWith('\n'))
            sb.Append('\n');

        foreach (var dlc in dlcAppIds)
        {
            var line = $"addappid({dlc})";
            if (!existingLua.Contains(line))
                sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }
}
