namespace OpenSteamUnlocker.Core.Http;

/// <summary>
/// Default <see cref="IGitHubMirrorClient"/> implementation.
/// </summary>
public sealed class GitHubMirrorClient : IGitHubMirrorClient
{
    private readonly HttpClient _http;
    private readonly IRegionDetector _region;
    private int? _preferredMirrorIndex;

    public GitHubMirrorClient(HttpClient http, IRegionDetector region)
    {
        _http = http;
        _region = region;
    }

    public IReadOnlyList<string> BuildMirrorUrls(string repo, string sha, string path)
    {
        if (_region.IsChina)
        {
            return new[]
            {
                $"https://gh-proxy.org/https://github.com/{repo}/{sha}/{path}",
                $"https://cdn.gh-proxy.org/https://github.com/{repo}/{sha}/{path}",
                $"https://edgeone.gh-proxy.org/https://github.com/{repo}/{sha}/{path}",
                $"https://github.chenc.dev/github.com/{repo}/{sha}/{path}",
                $"https://fastgit.cc/https://github.com/{repo}/{sha}/{path}",
                $"https://gh.llkk.cc/https://github.com/{repo}/{sha}/{path}",
                $"https://gh.akass.cn/{repo}/{sha}/{path}",
                $"https://raw.githubusercontent.com/{repo}/{sha}/{path}",
            };
        }

        return new[] { $"https://raw.githubusercontent.com/{repo}/{sha}/{path}" };
    }

    public async Task<byte[]> GetFromMirrorsAsync(string repo, string sha, string path, CancellationToken ct = default)
    {
        var originalUrls = BuildMirrorUrls(repo, sha, path);

        // Move the last-successful mirror to the front.
        var ordered = originalUrls.ToList();
        if (_preferredMirrorIndex is int pref && pref > 0 && pref < ordered.Count)
        {
            var preferredUrl = ordered[pref];
            ordered.RemoveAt(pref);
            ordered.Insert(0, preferredUrl);
        }

        foreach (var url in ordered)
        {
            try
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var idx = originalUrls.ToList().IndexOf(url);
                    if (idx >= 0)
                        _preferredMirrorIndex = idx;
                    return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException)
            {
                // Try the next mirror.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-request timeout: try the next mirror.
            }
        }

        throw new HttpRequestException($"Failed to download '{path}' after trying all mirrors.");
    }
}
