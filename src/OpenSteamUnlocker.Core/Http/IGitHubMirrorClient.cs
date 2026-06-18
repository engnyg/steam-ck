namespace OpenSteamUnlocker.Core.Http;

/// <summary>
/// Downloads raw files from a GitHub repo, transparently falling back across a
/// list of mirror proxies when inside China. Ported from
/// <c>_build_mirror_urls</c> / <c>_get_from_mirrors</c>.
/// </summary>
public interface IGitHubMirrorClient
{
    /// <summary>Candidate URLs for a raw file, mirrors first when in China.</summary>
    IReadOnlyList<string> BuildMirrorUrls(string repo, string sha, string path);

    /// <summary>
    /// Downloads a raw file, trying each mirror in turn. Remembers the mirror
    /// that last succeeded and tries it first next time.
    /// </summary>
    /// <exception cref="HttpRequestException">All mirrors failed.</exception>
    Task<byte[]> GetFromMirrorsAsync(string repo, string sha, string path, CancellationToken ct = default);
}
