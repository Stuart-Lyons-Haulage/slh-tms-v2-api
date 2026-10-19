using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RequestLatencyMonitorTests
{
    [Fact]
    public void Ranks_endpoints_by_p95_and_keeps_slow_calls()
    {
        var monitor = new RequestLatencyMonitor();
        for (var i = 0; i < 20; i++) monitor.Record("GET", "/api/v1/fast", 40, 200);
        for (var i = 0; i < 20; i++) monitor.Record("GET", "/api/v1/slow", 3000, 200);

        var summary = monitor.Read(DateTimeOffset.UtcNow);
        Assert.Equal("GET /api/v1/slow", summary.Endpoints[0].Endpoint);
        Assert.True(summary.Endpoints[0].P95Ms >= summary.Endpoints[1].P95Ms);
        Assert.Contains(summary.RecentSlow, call => call.Route == "/api/v1/slow" && call.Ms >= 1500);
        Assert.DoesNotContain(summary.RecentSlow, call => call.Route == "/api/v1/fast");
    }

    [Fact]
    public void Diagnostics_endpoints_are_not_measured()
    {
        var monitor = new RequestLatencyMonitor();
        monitor.Record("GET", "/api/v1/diagnostics/slow-endpoints", 5000, 200);
        Assert.Empty(monitor.Read(DateTimeOffset.UtcNow).Endpoints);
    }
}
