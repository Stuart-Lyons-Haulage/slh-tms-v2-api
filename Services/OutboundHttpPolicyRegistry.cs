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
        var isSamsara = string.Equals(upstreamService, "Samsara", StringComparison.OrdinalIgnoreCase);
        var handled = HttpPolicyExtensions.HandleTransientHttpError()
            .OrResult(response => response.StatusCode == HttpStatusCode.TooManyRequests);

        // Samsara route creation is a POST. Retrying a timed-out POST can create a
        // second route when the provider accepted the first request but the response
        // was lost. Route/address upserts already have external IDs and are safe to
        // repeat only when the caller explicitly retries them. Keep the generic retry
        // policy for other integrations, but do not automatically retry Samsara POSTs.
        var retry = isSamsara
            ? Policy<HttpResponseMessage>
                .HandleResult(response => response.RequestMessage?.Method != HttpMethod.Post &&
                                          IsTransient(response.StatusCode))
                .WaitAndRetryAsync(
                    retryCount: 1,
                    sleepDurationProvider: _ => TimeSpan.FromMilliseconds(250),
                    onRetry: (outcome, delay, attempt, _) => LogRetry(logger, upstreamService, outcome.Result, delay, attempt))
            : handled.WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)),
            onRetry: (outcome, delay, attempt, _) => LogRetry(logger, upstreamService, outcome.Result, delay, attempt));

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

    private static void LogRetry(
        ILogger logger,
        string upstreamService,
        HttpResponseMessage? response,
        TimeSpan delay,
        int attempt)
    {
        var status = response is null ? "exception" : ((int)response.StatusCode).ToString();
        logger.LogWarning(
            "{UpstreamService} transient HTTP failure ({Status}); retry {Attempt} in {DelaySeconds:F0}s.",
            upstreamService,
            status,
            attempt,
            delay.TotalSeconds);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout ||
        (int)statusCode >= 500;
}
