using System.Collections.Concurrent;
using System.Net;
using Polly;
using Polly.Extensions.Http;

namespace Slh.Tms.Api.Services;

public sealed class OutboundHttpPolicyRegistry(ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<string, IAsyncPolicy<HttpResponseMessage>> _policies = new(StringComparer.OrdinalIgnoreCase);

    public IAsyncPolicy<HttpResponseMessage> Get(string upstreamService) =>
        _policies.GetOrAdd(upstreamService, CreatePolicy);

    private IAsyncPolicy<HttpResponseMessage> CreatePolicy(string upstreamService)
    {
        var logger = loggerFactory.CreateLogger($"OutboundResilience.{upstreamService}");
        var handled = HttpPolicyExtensions.HandleTransientHttpError()
            .OrResult(response => response.StatusCode == HttpStatusCode.TooManyRequests);

        var retry = handled.WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)),
            onRetry: (outcome, delay, attempt, _) =>
            {
                var status = outcome.Result is null ? "exception" : ((int)outcome.Result.StatusCode).ToString();
                logger.LogWarning(
                    "{UpstreamService} transient HTTP failure ({Status}); retry {Attempt}/3 in {DelaySeconds:F0}s.",
                    upstreamService,
                    status,
                    attempt,
                    delay.TotalSeconds);
            });

        var breaker = handled.CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 5,
            durationOfBreak: TimeSpan.FromSeconds(30),
            onBreak: (outcome, duration) => logger.LogWarning(
                "{UpstreamService} circuit breaker OPEN for {DurationSeconds:F0}s after five consecutive failed operations. Last status: {Status}.",
                upstreamService,
                duration.TotalSeconds,
                outcome.Result is null ? "exception" : ((int)outcome.Result.StatusCode).ToString()),
            onReset: () => logger.LogWarning("{UpstreamService} circuit breaker RESET/CLOSED after a successful probe.", upstreamService),
            onHalfOpen: () => logger.LogWarning("{UpstreamService} circuit breaker HALF-OPEN; allowing a probe request.", upstreamService));

        // The breaker wraps the retry policy so one logical outbound operation counts as one
        // breaker success/failure after all three retries have been exhausted.
        return Policy.WrapAsync(breaker, retry);
    }
}
