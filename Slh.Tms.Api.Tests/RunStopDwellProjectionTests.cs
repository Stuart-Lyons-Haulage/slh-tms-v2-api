using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RunStopDwellProjectionTests
{
    [Fact]
    public void Arrival_exposes_live_time_on_site_from_geofence_entry()
    {
        var now = DateTimeOffset.Parse("2026-08-24T10:37:00Z");
        var (load, stopId) = LoadWithStop("Aldi Swindon");
        var visit = Visit(load.Id, stopId, now.AddMinutes(-37), null);

        var states = RunStopDwellProjection.Build(load, [visit], [visit], now);

        var state = Assert.Single(states);
        Assert.Equal("OnSite", state.State);
        Assert.Equal(now.AddMinutes(-37), state.SiteArrivalUtc);
        Assert.Null(state.SiteDepartureUtc);
        Assert.Equal(37 * 60, state.LiveDwellSeconds);
        Assert.Equal(37, state.LiveDwellMinutes);
        Assert.Null(state.FinalDwellSeconds);
    }

    [Fact]
    public void Departure_freezes_final_dwell_for_history()
    {
        var arrival = DateTimeOffset.Parse("2026-08-24T23:50:00Z");
        var departure = DateTimeOffset.Parse("2026-08-25T00:42:00Z");
        var (load, stopId) = LoadWithStop("Aldi Swindon");
        var visit = Visit(load.Id, stopId, arrival, departure);

        var state = Assert.Single(RunStopDwellProjection.Build(load, [visit], [], departure.AddMinutes(30)));

        Assert.Equal("Departed", state.State);
        Assert.Equal(arrival, state.SiteArrivalUtc);
        Assert.Equal(departure, state.SiteDepartureUtc);
        Assert.Equal(52 * 60, state.FinalDwellSeconds);
        Assert.Equal(52, state.FinalDwellMinutes);
        Assert.Null(state.LiveDwellSeconds);
    }

    [Fact]
    public async Task Duplicate_geofence_projection_upsert_does_not_reset_arrival()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        var arrival = DateTimeOffset.Parse("2026-08-24T08:00:00Z");
        var departure = DateTimeOffset.Parse("2026-08-24T08:42:00Z");
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        var visit = Visit(loadId, stopId, arrival, departure);
        var snapshot = new EmbeddedGeofenceSnapshot([visit.Fence], [visit], [], [visit], 2, departure);

        await RunStopDwellProjection.TryPersistAsync(db, snapshot, CancellationToken.None);
        await RunStopDwellProjection.TryPersistAsync(db, snapshot, CancellationToken.None);

        var row = Assert.Single(db.GeofenceVisits);
        Assert.Equal(visit.Id, row.Id);
        Assert.Equal(arrival, row.EnteredAtUtc);
        Assert.Equal(departure, row.ExitedAtUtc);
        Assert.Equal(42, row.DwellMinutes);
        Assert.Equal("Departed", row.Status);
    }

    [Fact]
    public void Unrelated_active_geofence_does_not_surface_run_linkage_exception()
    {
        var now = DateTimeOffset.Parse("2026-08-24T11:00:00Z");
        var vehicleId = Guid.NewGuid();
        var load = new Load
        {
            Id = Guid.NewGuid(),
            Reference = "RUN-UNRELATED",
            PlanningDate = DateOnly.FromDateTime(now.UtcDateTime),
            Status = LoadStatus.InProgress,
            VehicleId = vehicleId,
            Stops = [new LoadStop { Id = Guid.NewGuid(), Sequence = 1, Name = "Different Stop" }]
        };
        var visit = Visit(null, null, now.AddMinutes(-10), null, vehicleId);
        var snapshot = new EmbeddedGeofenceSnapshot([visit.Fence], [visit], [visit], [], 1, now);

        var dwell = Assert.Single(RunStopDwellProjection.Build(load, [], [visit], now));
        var exception = RunStopDwellProjection.LinkExceptionFor(load, snapshot);

        Assert.Equal("EnRoute", dwell.State);
        Assert.Null(exception);
    }

    [Fact]
    public void Planned_stop_geofence_still_surfaces_linkage_exception_when_assignment_is_missing()
    {
        var now = DateTimeOffset.Parse("2026-08-24T11:00:00Z");
        var vehicleId = Guid.NewGuid();
        var load = new Load
        {
            Id = Guid.NewGuid(),
            Reference = "RUN-UNLINKED",
            PlanningDate = DateOnly.FromDateTime(now.UtcDateTime),
            Status = LoadStatus.InProgress,
            VehicleId = vehicleId,
            Stops = [new LoadStop { Id = Guid.NewGuid(), Sequence = 1, Name = "Aldi Swindon" }]
        };
        var visit = Visit(null, null, now.AddMinutes(-10), null, vehicleId);
        var snapshot = new EmbeddedGeofenceSnapshot([visit.Fence], [visit], [visit], [], 1, now);

        var exception = RunStopDwellProjection.LinkExceptionFor(load, snapshot);

        Assert.NotNull(exception);
        Assert.Equal("Unlinked", exception!.State);
    }

    [Fact]
    public void Multiple_stops_retain_their_own_arrival_departure_and_dwell_values()
    {
        var stopOne = Guid.NewGuid();
        var stopTwo = Guid.NewGuid();
        var load = new Load
        {
            Id = Guid.NewGuid(),
            Reference = "RUN-MULTI",
            PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = LoadStatus.InProgress,
            Stops =
            [
                new LoadStop { Id = stopOne, Sequence = 1, Name = "Aldi Swindon" },
                new LoadStop { Id = stopTwo, Sequence = 2, Name = "Aldi Darlington" }
            ]
        };
        var first = Visit(load.Id, stopOne, DateTimeOffset.Parse("2026-08-24T08:00:00Z"), DateTimeOffset.Parse("2026-08-24T08:20:00Z"));
        var second = Visit(load.Id, stopTwo, DateTimeOffset.Parse("2026-08-24T10:00:00Z"), DateTimeOffset.Parse("2026-08-24T10:45:00Z"));

        var states = RunStopDwellProjection.Build(load, [first, second], [], DateTimeOffset.Parse("2026-08-24T11:00:00Z")).OrderBy(x => x.Sequence).ToList();

        Assert.Equal(20, states[0].FinalDwellMinutes);
        Assert.Equal(45, states[1].FinalDwellMinutes);
    }

    private static (Load Load, Guid StopId) LoadWithStop(string name)
    {
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        return (new Load
        {
            Id = loadId,
            Reference = "RUN-DWELL",
            PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = LoadStatus.InProgress,
            VehicleId = Guid.NewGuid(),
            Stops = [new LoadStop { Id = stopId, LoadId = loadId, Sequence = 1, Name = name }]
        }, stopId);
    }

    private static DerivedVisit Visit(Guid? loadId, Guid? stopId, DateTimeOffset arrival, DateTimeOffset? departure, Guid? vehicleId = null)
    {
        var fence = new EmbeddedFence(Guid.NewGuid(), "Aldi Swindon", "Delivery", null, null, 0, 0, null, []);
        return new DerivedVisit
        {
            Id = Guid.NewGuid(),
            VehicleId = vehicleId ?? Guid.NewGuid(),
            VehicleIdentifier = "AB12CDE",
            Fence = fence,
            LoadId = loadId,
            LoadStopId = stopId,
            EnteredAtUtc = arrival,
            ConfirmedAtUtc = arrival.AddMinutes(10),
            ExitedAtUtc = departure,
            LastInsideAtUtc = departure ?? arrival.AddMinutes(10),
            DwellMinutes = (int)Math.Floor(((departure ?? arrival.AddMinutes(10)) - arrival).TotalMinutes)
        };
    }
}
