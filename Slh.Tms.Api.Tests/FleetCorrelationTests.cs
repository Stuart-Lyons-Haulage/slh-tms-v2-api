using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Models.Tracking;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class FleetCorrelationTests
{
    [Theory]
    [InlineData("Daniel Williams", "DANIEL WILLIAMS")]
    [InlineData("Williams, Daniel", "DANIEL WILLIAMS")]
    [InlineData("  Daniel-J. Williams ", "DANIELJ WILLIAMS")]
    public void Tacho_names_are_normalised_independently_of_order_and_punctuation(string value, string expected)
    {
        Assert.Equal(expected, DotTrackingController.NormalisePersonName(value));
    }

    [Fact]
    public void Empty_tacho_name_has_no_correlation_key()
    {
        Assert.Equal(string.Empty, DotTrackingController.NormalisePersonName(null));
    }

    [Theory]
    [InlineData(true, false, false, "Moving")]
    [InlineData(false, true, false, "Started")]
    [InlineData(false, false, true, "SignedOn")]
    [InlineData(false, false, false, "NotSignedOn")]
    public void Live_vehicle_condition_separates_movement_from_non_active_states(bool moving, bool ignition, bool hasDriverCard, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var live = new VehicleLiveStatus
        {
            VehicleIdentifier = "TEST-1",
            LastEventTimeUtc = now,
            IsMoving = moving,
            IgnitionOn = ignition,
            SpeedKph = moving ? 10 : 0
        };

        Assert.Equal(expected, DotTrackingController.DetermineCondition(live, hasDriverCard, now));
    }
}
