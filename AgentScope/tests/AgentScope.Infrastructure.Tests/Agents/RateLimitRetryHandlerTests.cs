using System.Net;
using AgentScope.Infrastructure.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentScope.Infrastructure.Tests.Agents;

public class RateLimitRetryHandlerTests
{
    [Fact]
    public async Task Retries_429_then_returns_success()
    {
        var inner = new ScriptedHandler(new[]
        {
            (HttpStatusCode.TooManyRequests, (double?)0.05),
            (HttpStatusCode.OK,              (double?)null)
        });

        using var client = new HttpClient(new RateLimitRetryHandler(inner, NullLogger<RateLimitRetryHandler>.Instance));

        var response = await client.GetAsync("https://example.com/v1/chat/completions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Retries_5xx_with_exponential_backoff()
    {
        var inner = new ScriptedHandler(new[]
        {
            (HttpStatusCode.ServiceUnavailable, (double?)null),
            (HttpStatusCode.OK,                 (double?)null)
        });

        using var client = new HttpClient(new RateLimitRetryHandler(inner, NullLogger<RateLimitRetryHandler>.Instance));

        var response = await client.GetAsync("https://example.com/v1/chat/completions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Does_not_retry_400()
    {
        // 400 means the request itself is wrong — retrying just wastes tokens.
        var inner = new ScriptedHandler(new[]
        {
            (HttpStatusCode.BadRequest, (double?)null)
        });

        using var client = new HttpClient(new RateLimitRetryHandler(inner, NullLogger<RateLimitRetryHandler>.Instance));

        var response = await client.GetAsync("https://example.com/v1/chat/completions");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        // Three 429s in a row — handler attempts the call 3 times and then surfaces
        // the last failed response rather than throwing.
        var inner = new ScriptedHandler(new[]
        {
            (HttpStatusCode.TooManyRequests, (double?)0.01),
            (HttpStatusCode.TooManyRequests, (double?)0.01),
            (HttpStatusCode.TooManyRequests, (double?)0.01)
        });

        using var client = new HttpClient(new RateLimitRetryHandler(inner, NullLogger<RateLimitRetryHandler>.Instance));

        var response = await client.GetAsync("https://example.com/v1/chat/completions");

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        inner.CallCount.Should().Be(3);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly (HttpStatusCode Status, double? RetryAfterSeconds)[] _script;

        public ScriptedHandler((HttpStatusCode, double?)[] script)
        {
            _script = script;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Math.Min(CallCount, _script.Length - 1);
            var (status, retryAfter) = _script[index];
            CallCount++;

            var response = new HttpResponseMessage(status);
            if (retryAfter is { } seconds)
            {
                response.Headers.TryAddWithoutValidation(
                    "Retry-After",
                    seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return Task.FromResult(response);
        }
    }
}
