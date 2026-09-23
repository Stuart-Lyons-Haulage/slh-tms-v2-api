using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationalGeofenceSemanticsTests
{
    [Theory]
    [InlineData("NWF Drayton", "Drayton (Natures Way)")]
    [InlineData("NWF-Drayton", "Natures Way Drayton")]
    [InlineData("NWF Merston", "Merston (Natures Way)")]
    [InlineData("NWF Selsey", "Selsey (Natures Way)")]
    [InlineData("NWF Runcton", "Runcton (Natures Way)")]
    public void Nwf_planner_labels_are_same_physical_site_as_dot_natures_way_names(string plannerName, string dotName)
    {
        var stop = new LoadStop { Id = Guid.NewGuid(), Sequence = 1, Name = plannerName };
        var fence = Fence(dotName);

        Assert.True(GeofencePlanningMatch.SamePhysicalSite(stop, fence));
    }

    [Fact]
    public void Nwf_alias_does_not_match_unrelated_same_locality_site()
    {
        var stop = new LoadStop { Id = Guid.NewGuid(), Sequence = 1, Name = "NWF Drayton" };
        var fence = Fence("Drayton Logistics Park");

        Assert.False(GeofencePlanningMatch.SamePhysicalSite(stop, fence));
    }

    [Theory]
    [InlineData("Lake Lane")]
    [InlineData("SLH Lake Lane Depot")]
    [InlineData("Lake-Lane Yard")]
    public void Lake_lane_names_are_recognised_as_operational_origin(string name)
    {
        Assert.True(GeofencePlanningMatch.IsLakeLaneFence(name));
    }

    [Fact]
    public void Lake_lane_departure_near_first_planned_stop_starts_first_leg()
    {
        var vehicleId = Guid.NewGuid();
        var load = new Load
        {
            Id = Guid.NewGuid(),
            Reference = "Run 1 AM",
            PlanningDate = new DateOnly(2026, 8, 25),
            Status = LoadStatus.Planned,
            VehicleId = vehicleId,
            Stops =
            [
                new LoadStop
                {
                    Id = Guid.NewGuid(),
                    Sequence = 1,
                    Name = "NWF Drayton",
                    PlannedArrivalUtc = new DateTimeOffset(2026, 8, 25, 4, 30, 0, TimeSpan.Zero)
                }
            ]
        };
        var lakeLane = Fence("SLH Lake Lane");
        var departure = new DerivedVisit
        {
            Id = Guid.NewGuid(),
            VehicleId = vehicleId,
            VehicleIdentifier = "TEST1",
            Fence = lakeLane,
            EnteredAtUtc = new DateTimeOffset(2026, 8, 25, 3, 0, 0, TimeSpan.Zero),
            LastInsideAtUtc = new DateTimeOffset(2026, 8, 25, 3, 45, 0, TimeSpan.Zero),
            ExitedAtUtc = new DateTimeOffset(2026, 8, 25, 3, 46, 0, TimeSpan.Zero)
        };
        var snapshot = Snapshot(departure);

        var resolved = OperationalRunOrigin.LakeLaneDepartureFor(snapshot, load);

        Assert.Same(departure, resolved);
    }

    [Fact]
    public void Old_lake_lane_departure_is_not_reused_for_a_later_run()
    {
        var vehicleId = Guid.NewGuid();
        var load = new Load
        {
            Id = Guid.NewGuid(),
            Reference = "Run 2 PM",
            PlanningDate = new DateOnly(2026, 8, 25),
            Status = LoadStatus.Planned,
            VehicleId = vehicleId,
            Stops =
            [
                new LoadStop
                {
                    Id = Guid.NewGuid(),
                    Sequence = 1,
                    Name = "Customer",
                    PlannedArrivalUtc = new DateTimeOffset(2026, 8, 25, 15, 0, 0, TimeSpan.Zero)
                }
            ]
        };
        var oldDeparture = new DerivedVisit
        {
            Id = Guid.NewGuid(),
            VehicleId = vehicleId,
            VehicleIdentifier = "TEST1",
            Fence = Fence("Lake Lane"),
            EnteredAtUtc = new DateTimeOffset(2026, 8, 25, 4, 0, 0, TimeSpan.Zero),
            LastInsideAtUtc = new DateTimeOffset(2026, 8, 25, 4, 15, 0, TimeSpan.Zero),
            ExitedAtUtc = new DateTimeOffset(2026, 8, 25, 4, 16, 0, TimeSpan.Zero)
        };

        Assert.Null(OperationalRunOrigin.LakeLaneDepartureFor(Snapshot(oldDeparture), load));
    }

    private static EmbeddedGeofenceSnapshot Snapshot(params DerivedVisit[] visits) =>
        new(
            visits.Select(visit => visit.Fence).DistinctBy(fence => fence.Id).ToList(),
            visits,
            visits.Where(visit => visit.ExitedAtUtc is null).ToList(),
            visits.Where(visit => visit.ConfirmedAtUtc is not null).ToList(),
            visits.Length,
            visits.Select(visit => (DateTimeOffset?)visit.LastInsideAtUtc).Max());

    private static EmbeddedFence Fence(string name) =>
        new(
            Guid.NewGuid(),
            name,
            "Test",
            null,
            null,
            0,
            0,
            null,
            [
                new GeoPoint(-0.1, 50.0),
                new GeoPoint(-0.09, 50.0),
                new GeoPoint(-0.09, 50.01),
                new GeoPoint(-0.1, 50.01)
            ]);
}