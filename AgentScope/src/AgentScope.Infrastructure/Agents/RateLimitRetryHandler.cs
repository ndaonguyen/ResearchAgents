using System.Net;
using Microsoft.Extensions.Logging;

namespace AgentScope.Infrastructure.Agents;

/// <summary>
/// Retries OpenAI requests that come back with a transient failure status:
///   * 429 Too Many Requests — honours the <c>Retry-After</c> header when present,
///     falls back to exponential backoff otherwise.
///   * 500 / 502 / 503 / 504 — exponential backoff only.
///
/// Without this handler a single rate-limit bump kills the whole orchestrator run
/// (see <c>Orchestrator.RunAsync</c>'s catch — it publishes <c>AgentErrorEvent</c>
/// and bails). With it, the pipeline pauses for ~8s on a typical 429 and then
/// continues. Other 4xx (400, 401, 404, …) are NOT retried — they mean the request
/// itself is wrong and retrying would only waste tokens.
///
/// Inserted between <see cref="OpenAiTrafficLogger"/> and <see cref="HttpClientHandler"/>
/// so retries appear in the traffic log (helps diagnose "did the retry actually fire?").
/// </summary>
public sealed class RateLimitRetryHandler : DelegatingHandler
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger<RateLimitRetryHandler> _logger;

    public RateLimitRetryHandler(HttpMessageHandler inner, ILogger<RateLimitRetryHandler> logger)
        : base(inner)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Each retry attempt needs to re-send the body, which means re-buffering
            // the content. HttpClient's default behavior with a single SendAsync would
            // consume content once; here we let the inner handler re-read since the
            // upstream OpenAI SDK provides streamable content.
            response = await base.SendAsync(request, cancellationToken);

            if (!ShouldRetry(response.StatusCode) || attempt == MaxAttempts)
            {
                return response;
            }

            var delay = ComputeDelay(response, attempt);
            _logger.LogWarning(
                "Transient {Status} from {Url} (attempt {Attempt}/{Max}); retrying after {Delay}",
                (int)response.StatusCode, request.RequestUri, attempt, MaxAttempts, delay);

            // Dispose the failed response before retrying — otherwise we leak the
            // underlying connection until the GC catches up.
            response.Dispose();

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Caller cancelled while we were sleeping — fail fast instead of retrying.
                throw;
            }
        }

        return response!;
    }

    private static bool ShouldRetry(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.InternalServerError => true,
        HttpStatusCode.BadGateway => true,
        HttpStatusCode.ServiceUnavailable => true,
        HttpStatusCode.GatewayTimeout => true,
        _ => false
    };

    /// <summary>
    /// For 429: prefer the server's <c>Retry-After</c> hint (OpenAI returns this in seconds
    /// to fractional-second precision). For 5xx or missing header: exponential backoff
    /// 1s → 2s → 4s, capped at <see cref="MaxDelay"/>.
    /// </summary>
    private static TimeSpan ComputeDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                return Clamp(delta);
            }
            if (retryAfter.Date is { } date)
            {
                var delay = date - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? Clamp(delay) : TimeSpan.FromSeconds(1);
            }
        }

        // OpenAI sometimes returns the delay as a raw decimal in the header value rather
        // than via HttpClient's typed RetryConditionHeaderValue. Probe the raw string too.
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var raw in values)
            {
                if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    return Clamp(TimeSpan.FromSeconds(seconds));
                }
            }
        }

        // Exponential fallback: 2^(attempt-1) seconds.
        return Clamp(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
    }

    private static TimeSpan Clamp(TimeSpan delay) => delay > MaxDelay ? MaxDelay : delay;
}
