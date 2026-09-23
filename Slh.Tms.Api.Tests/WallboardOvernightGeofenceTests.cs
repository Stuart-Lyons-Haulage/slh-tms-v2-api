using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class WallboardOvernightGeofenceTests
{
    [Fact]
    public void Previous_evening_preload_matches_current_operating_day_by_vehicle_not_driver()
    {
        var vehicleId = Guid.NewGuid();
        var loadId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var load = new Load
        {
            Id = loadId,
            Reference = "RUN-OVERNIGHT",
            PlanningDate = new DateOnly(2026, 9, 8),
            Status = LoadStatus.Planned,
            VehicleId = vehicleId,
            Stops =
            [
                new LoadStop { Id = collectionId, LoadId = loadId, Sequence = 1, Name = "Collect · Aldi Swindon", PlannedArrivalUtc = DateTimeOffset.Parse("2026-09-08T18:00:00Z") },
                new LoadStop { Id = Guid.NewGuid(), LoadId = loadId, Sequence = 2, Name = "Deliver · Morrisons Stockton", PlannedArrivalUtc = DateTimeOffset.Parse("2026-09-08T06:00:00Z") }
            ]
        };
        var fence = Fence("Aldi Swindon");
        var visit = new GeofenceVisit
        {
            Id = Guid.NewGuid(),
            GeofenceId = fence.Id,
            VehicleId = vehicleId,
            VehicleIdentifier = "BL70RLO",
            EnteredAtUtc = DateTimeOffset.Parse("2026-09-07T18:05:00Z"),
            LastInsideAtUtc = DateTimeOffset.Parse("2026-09-07T18:45:00Z"),
            ExitedAtUtc = DateTimeOffset.Parse("2026-09-07T18:45:00Z"),
            Status = "Departed"
        };

        var match = VehiclePreloadGeofenceMatch.Match(visit, fence, [load]);

        Assert.NotNull(match);
        Assert.Equal(loadId, match!.Load.Id);
        Assert.Equal(collectionId, match.Stop.Id);
        Assert.Equal(1, match.Sequence);
        var window = VehiclePreloadGeofenceMatch.HistoryWindow(load.PlanningDate);
        Assert.True(visit.EnteredAtUtc >= window.StartUtc && visit.EnteredAtUtc < window.EndUtc);
    }

    [Fact]
    public void Ambiguous_same_vehicle_preload_is_not_guessed()
    {
        var vehicleId = Guid.NewGuid();
        var fence = Fence("Aldi Swindon");
        var first = LoadAt("RUN-A", vehicleId, DateTimeOffset.Parse("2026-09-08T08:00:00Z"));
        var second = LoadAt("RUN-B", vehicleId, DateTimeOffset.Parse("2026-09-08T08:15:00Z"));
        var visit = new GeofenceVisit
        {
            GeofenceId = fence.Id,
            VehicleId = vehicleId,
            VehicleIdentifier = "BL70RLO",
            EnteredAtUtc = DateTimeOffset.Parse("2026-09-08T08:05:00Z"),
            LastInsideAtUtc = DateTimeOffset.Parse("2026-09-08T08:25:00Z"),
            Status = "OnSite"
        };

        Assert.Null(VehiclePreloadGeofenceMatch.Match(visit, fence, [first, second]));
    }

    [Fact]
    public void Dwell_projection_resequences_legacy_pairs_collection_first_so_final_arrival_is_final()
    {
        var loadId = Guid.NewGuid();
        var collectOne = Guid.NewGuid();
        var deliverOne = Guid.NewGuid();
        var collectTwo = Guid.NewGuid();
        var deliverTwo = Guid.NewGuid();
        var load = new Load
        {
            Id = loadId,
            Reference = "RUN-LEGACY-PAIR",
            PlanningDate = new DateOnly(2026, 9, 8),
            Status = LoadStatus.InProgress,
            VehicleId = Guid.NewGuid(),
            Stops =
            [
                new LoadStop { Id = collectOne, LoadId = loadId, Sequence = 1, Name = "Collect · Greenhouse" },
                new LoadStop { Id = deliverOne, LoadId = loadId, Sequence = 2, Name = "Deliver · Darlington" },
                new LoadStop { Id = collectTwo, LoadId = loadId, Sequence = 3, Name = "Collect · Selsey" },
                new LoadStop { Id = deliverTwo, LoadId = loadId, Sequence = 4, Name = "Deliver · Morrisons Stockton" }
            ]
        };
        var now = DateTimeOffset.Parse("2026-09-08T09:00:00Z");
        var visit = new DerivedVisit
        {
            Id = Guid.NewGuid(),
            VehicleId = load.VehicleId!.Value,
            VehicleIdentifier = "BL70RLO",
            Fence = Fence("Morrisons Stockton"),
            LoadId = loadId,
            LoadStopId = deliverTwo,
            EnteredAtUtc = now.AddMinutes(-20),
            ConfirmedAtUtc = now.AddMinutes(-10),
            LastInsideAtUtc = now,
            DwellMinutes = 20
        };

        var states = RunStopDwellProjection.Build(load, [visit], [visit], now);

        Assert.Equal(new[] { collectOne, collectTwo, deliverOne, deliverTwo }, states.OrderBy(state => state.Sequence).Select(state => state.StopId));
        var final = Assert.Single(states.Where(state => state.StopId == deliverTwo));
        Assert.Equal(4, final.Sequence);
        Assert.Equal("OnSite", final.State);
    }

    private static Load LoadAt(string reference, Guid vehicleId, DateTimeOffset planned) => new()
    {
        Id = Guid.NewGuid(),
        Reference = reference,
        PlanningDate = new DateOnly(2026, 9, 8),
        Status = LoadStatus.Planned,
        VehicleId = vehicleId,
        Stops = [new LoadStop { Id = Guid.NewGuid(), Sequence = 1, Name = "Collect · Aldi Swindon", PlannedArrivalUtc = planned }]
    };

    private static EmbeddedFence Fence(string name) => new(Guid.NewGuid(), name, null, null, null, 0, 0, null, []);
}
