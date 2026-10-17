using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Short-lived process-wide cache for successful TachoMaster HTTP responses.
/// Multiple TMS screens asking for the same duty/member/metric snapshot within the
/// refresh window receive byte-for-byte identical provider data instead of creating
/// parallel RoadTech requests. Errors and rate-limit responses are never cached.
/// </summary>
public sealed class TachoMasterResponseCacheHandler(ILogger<TachoMasterResponseCacheHandler> logger) : DelegatingHandler
{
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, CachedResponse> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = await BuildKeyAsync(request, cancellationToken);
        if (TryRead(key, out var cached))
            return cached.CreateResponse();

        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryRead(key, out cached))
                return cached.CreateResponse();

            var response = await base.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return response;

            var stored = await CachedResponse.CaptureAsync(response, cancellationToken);
            Cache[key] = stored with { ExpiresAtUtc = DateTimeOffset.UtcNow.Add(SnapshotTtl) };
            logger.LogDebug("Cached TachoMaster {Method} {Path} response for {Seconds:0} seconds.", request.Method, request.RequestUri?.AbsolutePath, SnapshotTtl.TotalSeconds);
            return response;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool TryRead(string key, out CachedResponse cached)
    {
        if (Cache.TryGetValue(key, out cached!) && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            return true;

        Cache.TryRemove(key, out _);
        cached = default!;
        return false;
    }

    private static async Task<string> BuildKeyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var material = $"{request.Method.Method}|{request.RequestUri?.AbsolutePath}|{body}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private sealed record CachedResponse(
        HttpStatusCode StatusCode,
        string? ReasonPhrase,
        Version Version,
        byte[] Content,
        IReadOnlyList<KeyValuePair<string, string[]>> Headers,
        IReadOnlyList<KeyValuePair<string, string[]>> ContentHeaders,
        DateTimeOffset ExpiresAtUtc)
    {
        public static async Task<CachedResponse> CaptureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var content = response.Content is null
                ? []
                : await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new CachedResponse(
                response.StatusCode,
                response.ReasonPhrase,
                response.Version,
                content,
                response.Headers.Select(header => new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToArray(),
                response.Content?.Headers.Select(header => new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToArray() ?? [],
                DateTimeOffset.MinValue);
        }

        public HttpResponseMessage CreateResponse()
        {
            var response = new HttpResponseMessage(StatusCode)
            {
                ReasonPhrase = ReasonPhrase,
                Version = Version,
                Content = new ByteArrayContent(Content)
            };
            foreach (var header in Headers)
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            foreach (var header in ContentHeaders)
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return response;
        }
    }
}
