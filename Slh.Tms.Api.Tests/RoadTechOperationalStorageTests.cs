using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RoadTechOperationalStorageTests
{
    [Fact]
    public async Task Current_polls_update_one_live_row_without_retaining_breadcrumb_history()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        var now = DateTimeOffset.UtcNow;

        await store.PersistAsync(
        [
            Record("first", now.AddMinutes(-1), 51.0m, -1.0m),
            Record("second", now, 51.1m, -1.1m)
        ], CancellationToken.None);

        var state = Assert.Single(await db.VehicleLiveStatuses.ToListAsync());
        Assert.Equal(51.1m, state.Latitude);
        Assert.Equal(-1.1m, state.Longitude);
        Assert.Empty(await db.VehicleTrackingEvents.ToListAsync());
    }

    [Fact]
    public async Task Geofence_crossing_persists_arrival_and_departure_as_one_operational_visit()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        var siteId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Vehicles.Add(new Vehicle { Registration = "AB12CDE", Active = true });
        db.Sites.Add(new Site { Id = siteId, Name = "Test depot", ExternalCode = "TEST", Active = true });
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Test depot", NormalizedName = "TEST DEPOT", SiteId = siteId,
            PolygonJson = "[[0,0],[0,2],[2,2],[2,0]]"
        });
        await db.SaveChangesAsync();

        await GeofenceRunProgression.ProcessTelemetryAsync(db, [Record("in", now, 1m, 1m)], CancellationToken.None);
        await GeofenceRunProgression.ProcessTelemetryAsync(db, [Record("out", now.AddMinutes(2), 3m, 3m)], CancellationToken.None);

        var visit = Assert.Single(await db.GeofenceVisits.ToListAsync());
        Assert.Equal(siteId, visit.SiteId);
        Assert.Equal(now, visit.EnteredAtUtc);
        Assert.Equal(now.AddMinutes(2), visit.ExitedAtUtc);
        Assert.Equal(2, visit.DwellMinutes);
    }

    private static DotTelemetryRecord Record(string id, DateTimeOffset at, decimal latitude, decimal longitude) =>
        new(id, "AB12CDE", at, latitude, longitude, 20, true, true, "Received", "{}");
}
