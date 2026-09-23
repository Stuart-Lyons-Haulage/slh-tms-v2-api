using System.Collections.Concurrent;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Middleware;

public sealed class PlanningChangeNotificationMiddleware(RequestDelegate next)
{
    private sealed record CachedResponse(DateTimeOffset ExpiresAtUtc, int StatusCode, string? ContentType, byte[] Body);
    private static readonly ConcurrentDictionary<string, CachedResponse> ReadCache = new(StringComparer.Ordinal);
    private static readonly string[] PlanningPrefixes =
    [
        "/api/v1/planning-control",
        "/api/v1/runs",
        "/api/v1/loads",
        "/api/v1/driver-dispatch",
        "/api/v1/staging",
        "/api/v1/order-intake",
        "/api/v1/orders",
        "/api/v1/planner-import"
    ];

    public async Task InvokeAsync(HttpContext context, PlanningChangeNotifier notifier)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var mutation = context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE";
        var relevantMutation = mutation && PlanningPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        var ttl = context.Request.Method == "GET" ? CacheTtl(path, context.User.Identity?.IsAuthenticated == true) : null;
        if (ttl is TimeSpan cacheTtl)
        {
            var cacheKey = $"{path}{context.Request.QueryString}";
            if (ReadCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                context.Response.StatusCode = cached.StatusCode;
                context.Response.ContentType = cached.ContentType;
                context.Response.Headers.CacheControl = "private, no-store";
                await context.Response.Body.WriteAsync(cached.Body, context.RequestAborted);
                return;
            }

            var originalBody = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await next(context);
                var body = buffer.ToArray();
                if (context.Response.StatusCode is >= 200 and < 300 && IsCacheableContent(context.Response.ContentType))
                    ReadCache[cacheKey] = new CachedResponse(DateTimeOffset.UtcNow.Add(cacheTtl), context.Response.StatusCode, context.Response.ContentType, body);
                context.Response.Body = originalBody;
                await originalBody.WriteAsync(body, context.RequestAborted);
            }
            finally
            {
                context.Response.Body = originalBody;
            }
            return;
        }

        await next(context);

        if (relevantMutation && context.Response.StatusCode is >= 200 and < 300)
        {
            // Operational read models are derived from the same planning/allocation state. Once a
            // write succeeds, any cached wallboard/progress result can be wrong immediately (for
            // example Dispatched still appearing as Awaiting Dispatch, or Completed as InProgress).
            // Clear the small in-process read cache before notifying clients so their next refresh
            // necessarily observes the committed state.
            ReadCache.Clear();
            notifier.Publish($"{context.Request.Method} {path}");
        }
    }

    private static TimeSpan? CacheTtl(string path, bool authenticated)
    {
        if (!authenticated) return null;
        if (path.Equals("/api/v1/operations/delivery-etas", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/run-progress", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/run-timing", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/tv-display/route-progress", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/tv-display/live-runs", StringComparison.OrdinalIgnoreCase))
            // These endpoints drive live operational screens. A five-minute cache makes tracking,
            // dispatch and completion visibly stale even without a planner mutation (tracking is
            // also updated by background jobs), so keep only a very short coalescing window.
            return TimeSpan.FromSeconds(10);

        if (path.StartsWith("/api/v1/operations-intelligence", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/operations/reconciliation", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/operations/exceptions", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromSeconds(10);

        return null;
    }

    private static bool IsCacheableContent(string? contentType)
        => string.IsNullOrWhiteSpace(contentType) || contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
}
