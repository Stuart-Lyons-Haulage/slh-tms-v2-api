using System.Diagnostics;

namespace Slh.Tms.Api.Services;

public sealed class DependencyTelemetryHandler(TmsMetrics metrics) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            metrics.RecordDependencyLatency(Stopwatch.GetElapsedTime(started).TotalMilliseconds, request.RequestUri?.Host ?? "unknown", request.Method.Method, (int)response.StatusCode);
            return response;
        }
        catch
        {
            metrics.RecordDependencyLatency(Stopwatch.GetElapsedTime(started).TotalMilliseconds, request.RequestUri?.Host ?? "unknown", request.Method.Method, 599);
            throw;
        }
    }
}
