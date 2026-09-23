using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class GeofencePlanningMatchAccuracyTests
{
    [Fact]
    public void Entry_and_exit_over_two_minutes_counts_as_completed_when_stationary_provider_event_was_not_repeated()
    {
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        var fence = EmbeddedGeofenceEngine.ApprovedFences.First();
        var entered = DateTimeOffset.UtcNow.AddMinutes(-20);
        var load = new Load
        {
            Id = loadId,
            Reference = "RUN-TEST",
            PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Stops = [new LoadStop { Id = stopId, LoadId = loadId, Sequence = 1, Name = fence.Name }]
        };
        var visit = new DerivedVisit
        {
            Id = Guid.NewGuid(),
            VehicleId = Guid.NewGuid(),
            VehicleIdentifier = "AB12CDE",
            Fence = fence,
            LoadId = loadId,
            LoadStopId = stopId,
            EnteredAtUtc = entered,
            LastInsideAtUtc = entered,
            ExitedAtUtc = entered.AddMinutes(12),
            ConfirmedAtUtc = null,
            DwellMinutes = 0
        };

        var completed = GeofencePlanningMatch.CompletedStopIds(load, [visit]);

        Assert.Contains(stopId, completed);
    }

    [Fact]
    public void Short_linked_collection_exit_counts_as_completed_so_eta_does_not_route_backwards()
    {
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        var fence = EmbeddedGeofenceEngine.ApprovedFences.First();
        var entered = DateTimeOffset.UtcNow.AddMinutes(-5);
        var load = new Load
        {
            Id = loadId,
            Reference = "RUN-TEST",
            PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Stops = [new LoadStop { Id = stopId, LoadId = loadId, Sequence = 1, Name = fence.Name }]
        };
        var visit = new DerivedVisit
        {
            Id = Guid.NewGuid(),
            VehicleId = Guid.NewGuid(),
            VehicleIdentifier = "AB12CDE",
            Fence = fence,
            LoadId = loadId,
            LoadStopId = stopId,
            EnteredAtUtc = entered,
            LastInsideAtUtc = entered,
            ExitedAtUtc = entered.AddSeconds(45),
            ConfirmedAtUtc = null,
            DwellMinutes = 0
        };

        var completed = GeofencePlanningMatch.CompletedStopIds(load, [visit]);

        Assert.Contains(stopId, completed);
    }

    [Fact]
    public void Very_brief_pass_through_does_not_complete_a_stop()
    {
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        var fence = EmbeddedGeofenceEngine.ApprovedFences.First();
        var entered = DateTimeOffset.UtcNow.AddMinutes(-5);
        var load = new Load
        {
            Id = loadId,
            Reference = "RUN-TEST",
            PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Stops = [new LoadStop { Id = stopId, LoadId = loadId, Sequence = 1, Name = fence.Name }]
        };
        var visit = new DerivedVisit
        {
            Id = Guid.NewGuid(),
            VehicleId = Guid.NewGuid(),
            VehicleIdentifier = "AB12CDE",
            Fence = fence,
            LoadId = loadId,
            LoadStopId = stopId,
            EnteredAtUtc = entered,
            LastInsideAtUtc = entered,
            ExitedAtUtc = entered.AddSeconds(10),
            ConfirmedAtUtc = null,
            DwellMinutes = 0
        };

        var completed = GeofencePlanningMatch.CompletedStopIds(load, [visit]);

        Assert.DoesNotContain(stopId, completed);
    }

    [Fact]
    public void Prepared_stop_uses_falcon_fence_when_planner_coordinate_is_inside_polygon()
    {
        var fence = EmbeddedGeofenceEngine.ApprovedFences.First();
        var longitude = fence.Points.Average(point => point.Longitude);
        var latitude = fence.Points.Average(point => point.Latitude);
        var loadId = Guid.NewGuid();
        var load = new Load
        {
            Id = loadId,
            Reference = "RUN-COORDINATE",
            PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Stops =
            [
                new LoadStop
                {
                    Id = Guid.NewGuid(),
                    LoadId = loadId,
                    Sequence = 1,
                    Name = "Planner wording does not match Falcon",
                    Latitude = (decimal)latitude,
                    Longitude = (decimal)longitude
                }
            ]
        };

        var prepared = GeofencePlanningMatch.PrepareLoad(load);

        Assert.Equal(fence.Name, prepared.Stops[0].Name);
    }
}
