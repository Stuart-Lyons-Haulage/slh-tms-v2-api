using System.Collections.Concurrent;
using System.Net;

namespace Slh.Tms.Api.Services;

/// <summary>Small dependency-free resilience policy for provider calls that do not have a specialised client policy.</summary>
public sealed class ProviderResilienceHandler(ILogger<ProviderResilienceHandler> logger) : DelegatingHandler
{
    private static readonly ConcurrentDictionary<string, CircuitState> Circuits = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OpenInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromMinutes(1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri?.Host ?? "unknown";
        var circuit = Circuits.GetOrAdd(key, _ => new CircuitState());
        if (circuit.OpenedUntilUtc > DateTimeOffset.UtcNow)
            throw new HttpRequestException($"Provider circuit is open for {key} until {circuit.OpenedUntilUtc:O}.");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(AttemptTimeout);
                var response = await base.SendAsync(await CloneRequestAsync(request), timeout.Token);

                // A 429 is an explicit instruction to reduce request frequency. Retrying it
                // immediately amplifies the provider throttle, so return the first response and
                // open a short local circuit instead. The next scheduled refresh can try again.
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var backoff = response.Headers.RetryAfter?.Delta ?? DefaultRateLimitBackoff;
                    if (backoff < TimeSpan.FromSeconds(30)) backoff = TimeSpan.FromSeconds(30);
                    if (backoff > TimeSpan.FromMinutes(10)) backoff = TimeSpan.FromMinutes(10);
                    circuit.Open(backoff);
                    logger.LogWarning("Provider {ProviderHost} returned HTTP 429; suppressing retries for {BackoffSeconds:0} seconds.", key, backoff.TotalSeconds);
                    return response;
                }

                if (!IsTransient(response.StatusCode) || attempt == 3)
                {
                    if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.RequestTimeout)
                        circuit.Reset();
                    else
                        circuit.RecordFailure(OpenInterval);
                    return response;
                }
                response.Dispose();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                circuit.RecordFailure(OpenInterval);
                if (attempt == 3) throw new TimeoutException($"Provider request to {key} exceeded the {AttemptTimeout.TotalSeconds:0}-second attempt budget.");
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                circuit.RecordFailure(OpenInterval);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
            logger.LogDebug("Retrying provider request to {ProviderHost}; attempt {Attempt} of 3.", key, attempt + 1);
        }

        throw new InvalidOperationException("Provider resilience policy exhausted without a response.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout || (int)statusCode >= 500;

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private sealed class CircuitState
    {
        private int failures;
        public DateTimeOffset OpenedUntilUtc { get; private set; }
        public void RecordFailure(TimeSpan interval) { if (Interlocked.Increment(ref failures) >= 5) OpenedUntilUtc = DateTimeOffset.UtcNow.Add(interval); }
        public void Open(TimeSpan interval) { OpenedUntilUtc = DateTimeOffset.UtcNow.Add(interval); }
        public void Reset() { Interlocked.Exchange(ref failures, 0); OpenedUntilUtc = default; }
    }
}
