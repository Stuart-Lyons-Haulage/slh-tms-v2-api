using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class WallboardPhysicalStopsTests
{
    [Fact]
    public void Consecutive_order_lines_at_one_collection_site_become_one_wallboard_visit()
    {
        var stops = new[]
        {
            new LoadStop { Sequence = 1, Name = "Collect · NWF - Runcton", OrderId = Guid.NewGuid() },
            new LoadStop { Sequence = 2, Name = "Collect · NWF - Runcton", OrderId = Guid.NewGuid() },
            new LoadStop { Sequence = 3, Name = "Deliver · Bracknell", OrderId = Guid.NewGuid() },
            new LoadStop { Sequence = 4, Name = "Deliver · Brinklow", OrderId = Guid.NewGuid() }
        };

        var result = WallboardPhysicalStops.Collapse(stops);

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "Collect · NWF - Runcton", "Deliver · Bracknell", "Deliver · Brinklow" }, result.Select(stop => stop.Name));
        Assert.Equal(new[] { 1, 2, 3 }, result.Select(stop => stop.Sequence));
    }

    [Fact]
    public void A_later_return_to_the_same_site_remains_a_separate_visit()
    {
        var stops = new[]
        {
            new LoadStop { Sequence = 1, Name = "Collect · Merston" },
            new LoadStop { Sequence = 2, Name = "Deliver · Cardiff" },
            new LoadStop { Sequence = 3, Name = "Collect · Merston" }
        };

        Assert.Equal(3, WallboardPhysicalStops.Collapse(stops).Count);
    }

    [Fact]
    public void Dated_internal_reference_is_not_shown_on_the_wallboard()
    {
        var load = new Load
        {
            Reference = "RUN 20260908 01",
            Stops = [new LoadStop { Sequence = 1, Name = "Collect · Runcton", PlannedArrivalUtc = new DateTimeOffset(2026, 9, 8, 4, 30, 0, TimeSpan.Zero) }]
        };

        Assert.Equal("Run 1 AM", RunDisplayLabel.For(load));
    }

    [Fact]
    public void Fresh_dot_position_and_stop_coordinates_allow_eta_without_geofence_evidence()
    {
        var now = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        var stop = new LoadStop { Name = "Deliver · Bracknell", Latitude = 51.41m, Longitude = -0.75m };

        Assert.True(LiveEtaEligibility.CanRoute((-0.80m, 51.30m), stop, now.AddMinutes(-2), now));
    }
}
