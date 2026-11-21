using System.Net;

namespace AkioExplorer.CloudProviders.Http;

/// <summary>Shared, pooled HTTP stack for cloud APIs (one connection pool for the whole app).</summary>
public static class CloudHttp
{
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient(CreateHandler(), disposeHandler: false)
    {
        Timeout = Timeout.InfiniteTimeSpan, // per-request timeouts come from cancellation tokens (large transfers)
    });

    public static HttpClient Client => SharedClient.Value;

    public static HttpMessageHandler CreateHandler() => new RetryHandler(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxConnectionsPerServer = 16,
    });
}

/// <summary>
/// Retries throttled (429) and transient (500/502/503/504, network) failures with exponential backoff and jitter,
/// honouring Retry-After. Only for requests whose content can be resent (no streamed uploads).
/// Marked non-user code: a request the app cancels on purpose (thumbnail scrolled away, preview moved on) throws
/// through this handler into HttpClient, which Visual Studio's Just My Code would otherwise report as
/// "user-unhandled" and break on – although the caller handles it.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public sealed class RetryHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const int MaxAttempts = 4;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        bool replayable = request.Content is null or ByteArrayContent or FormUrlEncodedContent or StringContent;
        for (int attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!IsTransient(response.StatusCode) || attempt >= MaxAttempts || !replayable) return response;
            }
            catch (HttpRequestException) when (attempt < MaxAttempts && replayable && !cancellationToken.IsCancellationRequested)
            {
            }

            TimeSpan delay = response?.Headers.RetryAfter?.Delta
                             ?? (response?.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                             ?? TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 250));
            response?.Dispose();
            await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
