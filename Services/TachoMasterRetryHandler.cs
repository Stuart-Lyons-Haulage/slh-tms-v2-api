using System.Net;

namespace Slh.Tms.Api.Services;

public sealed class TachoMasterRetryHandler(ILogger<TachoMasterRetryHandler> logger) : DelegatingHandler
{
    private const int MaxAttempts = 3;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var template = await BufferedRequest.CreateAsync(request, cancellationToken);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1)
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 2)), cancellationToken);

            using var retryRequest = template.Create();
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(retryRequest, cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < MaxAttempts)
            {
                logger.LogWarning(exception,
                    "TachoMaster request {Method} {Path} failed before a response was received; retrying attempt {NextAttempt}/{MaxAttempts}.",
                    request.Method, request.RequestUri?.AbsolutePath, attempt + 1, MaxAttempts);
                continue;
            }

            // HTTP 429 is an explicit provider back-pressure signal. Throw a dedicated
            // non-HttpRequestException so the client-level retry loop cannot multiply it.
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta;
                response.Dispose();
                logger.LogWarning(
                    "TachoMaster upstream returned HTTP 429 for {Method} {Path}; immediate retries suppressed.",
                    request.Method,
                    request.RequestUri?.AbsolutePath);
                throw new TachoMasterRateLimitException(request.RequestUri?.AbsolutePath, retryAfter);
            }

            if (!ShouldRetry(request.RequestUri?.AbsolutePath, response.StatusCode))
                return response;

            if (attempt == MaxAttempts)
            {
                var statusCode = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException(
                    $"TachoMaster upstream {request.RequestUri?.AbsolutePath ?? "request"} returned HTTP {(int)statusCode} ({statusCode}) after {MaxAttempts} attempts.",
                    null,
                    statusCode);
            }

            logger.LogWarning(
                "TachoMaster upstream returned HTTP {StatusCode} for {Method} {Path}; retrying attempt {NextAttempt}/{MaxAttempts}.",
                (int)response.StatusCode, request.Method, request.RequestUri?.AbsolutePath, attempt + 1, MaxAttempts);
            response.Dispose();
        }

        throw new HttpRequestException("TachoMaster request failed after retries.");
    }

    private static bool ShouldRetry(string? path, HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            return true;

        // TachoMaster historically uses HTTP 500 during one of its login password-format checks.
        // The client already falls through to its alternate password representation, so do not
        // retry that 500 three times. For authenticated data endpoints, however, a 500 is treated
        // as transient and retried before the sync is allowed to fail.
        return statusCode == HttpStatusCode.InternalServerError &&
               !string.Equals(path, "/api/auth/login", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BufferedRequest(
        HttpMethod method,
        Uri? requestUri,
        Version version,
        HttpVersionPolicy versionPolicy,
        IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> headers,
        byte[]? content,
        IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> contentHeaders)
    {
        public static async Task<BufferedRequest> CreateAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[]? content = null;
            IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> contentHeaders = [];
            if (request.Content is not null)
            {
                content = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                contentHeaders = request.Content.Headers.Select(header =>
                    new KeyValuePair<string, IEnumerable<string>>(header.Key, header.Value.ToArray())).ToArray();
            }

            var headers = request.Headers.Select(header =>
                new KeyValuePair<string, IEnumerable<string>>(header.Key, header.Value.ToArray())).ToArray();

            return new BufferedRequest(request.Method, request.RequestUri, request.Version, request.VersionPolicy, headers, content, contentHeaders);
        }

        public HttpRequestMessage Create()
        {
            var clone = new HttpRequestMessage(method, requestUri)
            {
                Version = version,
                VersionPolicy = versionPolicy
            };

            foreach (var header in headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (content is not null)
            {
                clone.Content = new ByteArrayContent(content);
                foreach (var header in contentHeaders)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}

public sealed class TachoMasterRateLimitException(string? path, TimeSpan? retryAfter)
    : Exception($"TachoMaster upstream {path ?? "request"} returned HTTP 429 (TooManyRequests). Immediate retries were suppressed{(retryAfter is null ? "." : $"; provider retry-after is {retryAfter.Value.TotalSeconds:0} seconds.")}")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
