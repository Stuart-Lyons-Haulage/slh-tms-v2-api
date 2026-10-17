using System.Diagnostics.Metrics;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TelemetryContractTests
{
    [Fact]
    public void Custom_meter_publishes_required_operational_instruments()
    {
        _ = TmsMetrics.Shared;
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TmsMetrics.MeterName)
                    names.Add(instrument.Name);
            }
        };
        listener.Start();

        var required = new[]
        {
            "api_endpoint_latency_ms",
            "sql_query_latency_ms",
            "dependency_request_latency_ms",
            "roadtech_data_age_seconds",
            "fleetio_last_successful_sync_age_seconds",
            "tachomaster_last_successful_sync_age_seconds",
            "email_order_intake_success_total",
            "email_order_intake_failure_total",
            "import_processed_total",
            "import_duplicate_total",
            "planning_recovery_used_total",
            "logical_duplicate_collapse_total"
        };

        foreach (var name in required)
            Assert.Contains(name, names);
    }

    [Fact]
    public void Dependency_status_thresholds_are_structured_and_deterministic()
    {
        var now = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
        Assert.Equal("Healthy", DependencyHealthService.Evaluate(true, now.AddMinutes(-4), now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)).Status);
        Assert.Equal("Degraded", DependencyHealthService.Evaluate(true, now.AddMinutes(-10), now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)).Status);
        Assert.Equal("Unavailable", DependencyHealthService.Evaluate(true, now.AddMinutes(-20), now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)).Status);
        Assert.Equal("Unavailable", DependencyHealthService.Evaluate(false, now, now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)).Status);
    }
}
