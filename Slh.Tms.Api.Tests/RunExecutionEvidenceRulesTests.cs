using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RunExecutionEvidenceRulesTests
{
    [Fact]
    public void Fresh_tacho_and_tracking_are_verified_live()
    {
        var now = DateTimeOffset.UtcNow;
        var tacho = new TachoVehicleDriverStatus(
            "V1", 1, "Driver", null, null, now.AddHours(-2), null,
            0, 0, 0, 0, 0, null, null, null, 120, null, null, null, null, null, null);
        Assert.Equal("VerifiedLive", RunExecutionEvidenceRules.EvidenceStatus(tacho, now.AddMinutes(-2), now));
    }

    [Fact]
    public void Tracking_older_than_customer_eta_threshold_is_stale()
    {
        var now = DateTimeOffset.UtcNow;
        var tacho = new TachoVehicleDriverStatus(
            "V1", 1, "Driver", null, null, now.AddHours(-2), null,
            0, 0, 0, 0, 0, null, null, null, 120, null, null, null, null, null, null);
        Assert.Equal("TrackingStale", RunExecutionEvidenceRules.EvidenceStatus(tacho, now.AddMinutes(-6), now));
    }

    [Fact]
    public void Tracking_without_tacho_is_not_presented_as_fully_verified()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("TrackingOnly", RunExecutionEvidenceRules.EvidenceStatus(null, now.AddMinutes(-2), now));
    }
}
