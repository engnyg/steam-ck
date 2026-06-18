using System.Text.Json;

namespace OpenSteamUnlocker.Core.Http;

/// <summary>
/// Default <see cref="IRegionDetector"/>. Queries kugou's iscn endpoint and
/// fails safe to "China" (use mirrors) when the request fails — parity with
/// cai_backend's <c>checkcn()</c>.
/// </summary>
public sealed class RegionDetector : IRegionDetector
{
    private readonly HttpClient _http;

    public RegionDetector(HttpClient http) => _http = http;

    public bool IsChina { get; private set; } = true;

    public async Task<bool> DetectAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var json = await _http.GetStringAsync(
                "https://mips.kugou.com/check/iscn?&format=json", cts.Token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            // "flag" is truthy (1/0 or true/false) when in China.
            var flag = doc.RootElement.GetProperty("flag");
            IsChina = flag.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => flag.GetInt32() != 0,
                _ => true,
            };
        }
        catch
        {
            // Cannot determine region → assume China and use mirrors.
            IsChina = true;
        }

        return IsChina;
    }
}
