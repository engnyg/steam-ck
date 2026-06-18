using System.Text.RegularExpressions;
using OpenSteamUnlocker.Core.Manifest.Models;

namespace OpenSteamUnlocker.Core.Manifest.Lua;

/// <summary>
/// Parses depot ids and decryption keys out of an existing Lua unlock script.
/// Ported from <c>parse_lua_file_for_depots</c>.
/// </summary>
public static partial class LuaManifestParser
{
    [GeneratedRegex(@"addappid\((\d+),\s*1,\s*""([^""]+)""\)")]
    private static partial Regex AddAppIdWithKeyRegex();

    [GeneratedRegex(@"setManifestid\((\d+),\s*""([^""]+)""\)")]
    private static partial Regex SetManifestIdRegex();

    /// <summary>Returns depot id → decryption key for keyed addappid lines.</summary>
    public static IReadOnlyList<DepotInfo> Parse(string luaContent)
    {
        var manifests = SetManifestIdRegex()
            .Matches(luaContent)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        var result = new List<DepotInfo>();
        foreach (Match match in AddAppIdWithKeyRegex().Matches(luaContent))
        {
            var depotId = match.Groups[1].Value;
            manifests.TryGetValue(depotId, out var manifestId);
            result.Add(new DepotInfo(depotId, match.Groups[2].Value, manifestId));
        }

        return result;
    }

    /// <summary>Parses a Lua file from disk; returns empty on error.</summary>
    public static IReadOnlyList<DepotInfo> ParseFile(string luaFilePath)
    {
        try
        {
            return File.Exists(luaFilePath)
                ? Parse(File.ReadAllText(luaFilePath))
                : Array.Empty<DepotInfo>();
        }
        catch
        {
            return Array.Empty<DepotInfo>();
        }
    }
}
