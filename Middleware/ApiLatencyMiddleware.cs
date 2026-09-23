using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Middleware;

public sealed class ApiLatencyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TmsMetrics metrics)
    {
        var started = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                ?? context.Request.Path.Value
                ?? "unknown";
            var statusCode = failed && context.Response.StatusCode < 400
                ? StatusCodes.Status500InternalServerError
                : context.Response.StatusCode;
            metrics.RecordApiEndpointLatency(elapsedMs, context.Request.Method, route, statusCode);

            if (context.Request.Path.Equals("/api/v1/order-intake/email", StringComparison.OrdinalIgnoreCase))
                metrics.RecordEmailOrderIntake(!failed && statusCode < 400);
        }
    }
}
