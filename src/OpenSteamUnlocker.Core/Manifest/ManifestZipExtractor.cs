using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using OpenSteamUnlocker.Core.Manifest.Providers;
using ValveKeyValue;

namespace OpenSteamUnlocker.Core.Manifest;

/// <summary>
/// Extracts depot keys and manifest files from a ZIP archive obtained from
/// any source (GitHub branch, Cysaw, codeload.github.com, etc.).
///
/// Handles two key formats:
///   • <c>key.vdf</c>  — ValveKeyValue structure: depots → depotId → DecryptionKey
///   • <c>*.lua</c>    — <c>addappid(depotId, 1, "key")</c> lines (Cysaw / SteamTools format)
/// </summary>
public static partial class ManifestZipExtractor
{
    /// <summary>
    /// Extracts depot keys and manifest files from individually-downloaded files
    /// (GitHub API tree path). The counterpart to <see cref="Extract"/> for ZIP archives.
    /// </summary>
    public static GitHubManifestData? ExtractFromFiles(
        List<string> paths, byte[]?[] downloads, bool skipManifests)
    {
        var depotKeys     = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestFiles = new List<GitHubManifestFile>();

        for (var i = 0; i < paths.Count; i++)
        {
            var name = Path.GetFileName(paths[i]);

            // --- key / config VDF (requires content) ---
            if (name.Equals("key.vdf", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("config.vdf", StringComparison.OrdinalIgnoreCase))
            {
                if (downloads[i] is null) continue;
                using var stream = new MemoryStream(downloads[i]!);
                foreach (var (k, v) in ParseKeyVdf(stream))
                    depotKeys.TryAdd(k, v);
                continue;
            }

            // --- *.manifest — parse depot ID + manifest ID FROM FILENAME ---
            // The binary content is only needed for depotcache copy; the manifest ID
            // itself is encoded in the filename (depotId_manifestId.manifest).
            // We record the manifest even when the download failed (content = null)
            // so that setManifestid lines are still generated correctly.
            if (!skipManifests && name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
            {
                var m = ManifestFilenameRegex().Match(name);
                if (!m.Success) continue;
                // Use empty byte array if download failed — ManifestInstaller skips
                // depotcache copy when CopyManifestFilesAsync sees 0-length content.
                manifestFiles.Add(new GitHubManifestFile(
                    m.Groups[1].Value, m.Groups[2].Value, downloads[i] ?? []));
            }
        }

        if (depotKeys.Count == 0 && manifestFiles.Count == 0) return null;
        return new GitHubManifestData(depotKeys, manifestFiles);
    }

    /// <summary>
    /// Extracts depot keys and manifest files from <paramref name="zipBytes"/>.
    /// Returns <c>null</c> when the archive contains no usable data.
    /// </summary>
    /// <param name="skipManifests">
    /// Pass <c>true</c> to skip downloading *.manifest binaries (auto-update mode).
    /// </param>
    public static GitHubManifestData? Extract(byte[] zipBytes, bool skipManifests = false)
    {
        var depotKeys     = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestFiles = new List<GitHubManifestFile>();

        using var ms      = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var name = entry.Name;

            if (name.Equals("key.vdf", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("config.vdf", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = entry.Open();
                foreach (var (k, v) in ParseKeyVdf(stream))
                    depotKeys.TryAdd(k, v);
                continue;
            }

            if (name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            {
                using var sr = new System.IO.StreamReader(entry.Open());
                var lua = sr.ReadToEnd();
                foreach (var (k, v) in ParseLuaKeys(lua))
                    depotKeys.TryAdd(k, v);
                continue;
            }

            if (!skipManifests &&
                name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
            {
                var m = ManifestFilenameRegex().Match(name);
                if (!m.Success) continue;
                using var stream = entry.Open();
                using var buf    = new MemoryStream();
                stream.CopyTo(buf);
                manifestFiles.Add(new GitHubManifestFile(
                    m.Groups[1].Value, m.Groups[2].Value, buf.ToArray()));
            }
        }

        if (depotKeys.Count == 0 && manifestFiles.Count == 0) return null;
        return new GitHubManifestData(depotKeys, manifestFiles);
    }

    // ── key.vdf ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<(string depotId, string key)> ParseKeyVdf(Stream stream)
    {
        var result = new List<(string, string)>();
        try
        {
            var kv       = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
            var document = kv.Deserialize(stream);

            // Navigate into "depots" wrapper if present.
            var container = document.Root;
            if (document.Root.TryGetValue("depots", out var depotsNode) && depotsNode.IsCollection)
                container = depotsNode;

            foreach (var entry in container.Children)
            {
                if (!entry.Value.IsCollection) continue;
                if (!entry.Value.TryGetValue("DecryptionKey", out var keyObj)) continue;
                var key = keyObj.ToString(CultureInfo.InvariantCulture)?.Trim();
                if (IsValidKey(key))
                    result.Add((entry.Key, key!));
            }
        }
        catch { /* malformed VDF — return partial */ }
        return result;
    }

    // ── *.lua (addappid lines) ────────────────────────────────────────────────

    private static IEnumerable<(string depotId, string key)> ParseLuaKeys(string lua)
    {
        foreach (Match m in LuaAddAppIdRegex().Matches(lua))
        {
            var key = m.Groups[2].Value.Trim();
            if (IsValidKey(key))
                yield return (m.Groups[1].Value, key);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static bool IsValidKey(string? key) =>
        key is { Length: 64 } && key.All(Uri.IsHexDigit);

    [GeneratedRegex(@"^(\d+)_(\w+)\.manifest$", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestFilenameRegex();

    // Matches: addappid(123456, 1, "aabbcc...")  (spaces around comma allowed)
    [GeneratedRegex(@"addappid\((\d+)\s*,\s*1\s*,\s*""([^""]+)""\)", RegexOptions.IgnoreCase)]
    private static partial Regex LuaAddAppIdRegex();
}
