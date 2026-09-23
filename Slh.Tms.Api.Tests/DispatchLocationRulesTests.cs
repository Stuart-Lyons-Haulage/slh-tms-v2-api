using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DispatchLocationRulesTests
{
    [Fact]
    public void Tacho_sign_off_prefers_latest_position_before_duty_end()
    {
        var dutyEnd = DateTimeOffset.Parse("2026-09-09T18:00:00Z");
        var events = new[]
        {
            Event("AB12 CDE", "2026-09-09T17:40:00Z", 53.800m, -1.550m),
            Event("AB12CDE", "2026-09-09T17:58:00Z", 53.810m, -1.560m),
            Event("AB12CDE", "2026-09-09T18:20:00Z", 53.900m, -1.700m)
        };

        var result = DispatchLocationRules.ResolveTachoSignOffPosition("AB12 CDE", dutyEnd, events);

        Assert.NotNull(result);
        Assert.Equal(53.810m, result!.Latitude);
        Assert.Equal(-1.560m, result.Longitude);
        Assert.Equal(DateTimeOffset.Parse("2026-09-09T17:58:00Z"), result.AtUtc);
        Assert.Equal("Tacho sign-off · AB12 CDE", result.Label);
    }

    [Fact]
    public void Tacho_sign_off_uses_nearby_after_end_position_only_when_no_pre_end_position_exists()
    {
        var dutyEnd = DateTimeOffset.Parse("2026-09-09T18:00:00Z");
        var events = new[]
        {
            Event("AB12CDE", "2026-09-09T18:12:00Z", 52.950m, -1.150m),
            Event("AB12CDE", "2026-09-09T19:30:00Z", 53.000m, -1.000m)
        };

        var result = DispatchLocationRules.ResolveTachoSignOffPosition("AB12CDE", dutyEnd, events);

        Assert.NotNull(result);
        Assert.Equal(DateTimeOffset.Parse("2026-09-09T18:12:00Z"), result!.AtUtc);
    }

    [Fact]
    public void Tacho_sign_off_does_not_use_stale_or_different_vehicle_position()
    {
        var dutyEnd = DateTimeOffset.Parse("2026-09-09T18:00:00Z");
        var events = new[]
        {
            Event("AB12CDE", "2026-09-08T01:00:00Z", 51.000m, -1.000m),
            Event("ZZ99ZZZ", "2026-09-09T17:59:00Z", 54.000m, -2.000m)
        };

        Assert.Null(DispatchLocationRules.ResolveTachoSignOffPosition("AB12CDE", dutyEnd, events));
    }

    private static VehicleTrackingEvent Event(string vehicle, string atUtc, decimal latitude, decimal longitude) => new()
    {
        ProviderName = "RoadTech Falcon",
        ProviderEventId = Guid.NewGuid().ToString("N"),
        VehicleIdentifier = vehicle,
        EventTimeUtc = DateTimeOffset.Parse(atUtc),
        Latitude = latitude,
        Longitude = longitude,
        RawPayload = "{}"
    };
}
