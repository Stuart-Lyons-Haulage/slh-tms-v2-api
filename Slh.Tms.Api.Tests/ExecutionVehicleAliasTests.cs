using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class ExecutionVehicleAliasTests
{
    [Fact]
    public void Registration_matches_falcon_suffix_identifier()
    {
        var aliases = ExecutionIdentityResolver.VehicleAliasVariants("KY71 CVP");

        Assert.True(ExecutionIdentityResolver.MatchesVehicleIdentifier(aliases, "CVP"));
        Assert.True(ExecutionIdentityResolver.MatchesVehicleIdentifier(aliases, "71CVP"));
        Assert.True(ExecutionIdentityResolver.MatchesVehicleIdentifier(aliases, "KY71CVP"));
    }

    [Fact]
    public void First_movement_uses_same_suffix_identity_as_live_tracking()
    {
        var signOn = DateTimeOffset.UtcNow.AddHours(-2);
        var events = new[]
        {
            Tracking("CVP", signOn.AddMinutes(4), 0),
            Tracking("CVP", signOn.AddMinutes(12), 28)
        };

        var first = ExecutionIdentityResolver.FirstMovement(
            ExecutionIdentityResolver.VehicleAliasVariants("KY71CVP"),
            events,
            signOn);

        Assert.Equal(signOn.AddMinutes(12), first);
    }

    private static VehicleTrackingEvent Tracking(string identifier, DateTimeOffset at, decimal speed) => new()
    {
        ProviderName = "RoadTech Falcon",
        ProviderEventId = $"{identifier}-{at:O}",
        VehicleIdentifier = identifier,
        EventTimeUtc = at,
        Latitude = 50.84m,
        Longitude = -0.78m,
        SpeedKph = speed,
        IsMoving = speed > 2,
        RawPayload = "{}",
        MatchStatus = "Received"
    };
}
