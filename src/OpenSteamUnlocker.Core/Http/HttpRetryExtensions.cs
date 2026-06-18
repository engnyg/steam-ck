using System.Net;

namespace OpenSteamUnlocker.Core.Http;

/// <summary>
/// Retry helpers ported from cai_backend's <c>http_get_safe</c>: escalating
/// timeouts and retries on transient server errors (429/502/503/504).
/// </summary>
public static class HttpRetryExtensions
{
    private static readonly HashSet<HttpStatusCode> RetryableStatuses = new()
    {
        HttpStatusCode.TooManyRequests,     // 429
        HttpStatusCode.BadGateway,          // 502
        HttpStatusCode.ServiceUnavailable,  // 503
        HttpStatusCode.GatewayTimeout,      // 504
    };

    /// <summary>
    /// Performs a GET with retry. Returns the successful response, or null if
    /// every attempt failed. The caller owns disposing the response.
    /// </summary>
    public static async Task<HttpResponseMessage?> GetWithRetryAsync(
        this HttpClient http,
        string url,
        int timeoutSeconds = 30,
        int maxRetries = 3,
        double retryDelaySeconds = 1.0,
        IDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            // Escalate the timeout on later attempts (capped at 60s).
            var current = attempt == 0
                ? timeoutSeconds
                : Math.Min(timeoutSeconds * (attempt + 1), 60);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (headers is not null)
                {
                    foreach (var (key, value) in headers)
                        request.Headers.TryAddWithoutValidation(key, value);
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(current));

                var response = await http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return response;

                var retryable = RetryableStatuses.Contains(response.StatusCode);
                response.Dispose();

                if (retryable && attempt < maxRetries - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds * (attempt + 1)), ct).ConfigureAwait(false);
                    continue;
                }

                return null;
            }
            catch (Exception) when (attempt < maxRetries - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }

        return null;
    }
}
