using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class GeofenceEvidenceResilienceTests
{
    [Fact]
    public async Task Unresolved_planner_site_falls_back_to_matching_physical_sql_geofence()
    {
        var embedded = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(fence => fence.Name == "Selsey Despatch"));
        var longitude = embedded.Points.Average(point => point.Longitude);
        var latitude = embedded.Points.Average(point => point.Latitude);
        var now = DateTimeOffset.UtcNow;
        var planningDate = UkDate(now);
        var vehicleId = Guid.NewGuid();
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        var linkedSite = new Site
        {
            Id = Guid.NewGuid(),
            ExternalCode = "SITE-0023",
            Name = "Barfoots Sefter",
            DriverTextName = "BAR-Sefter South (+3°C)",
            Active = true
        };

        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);

        db.Sites.Add(linkedSite);
        db.Vehicles.Add(new Vehicle { Id = vehicleId, Registration = "SLH225", Active = true });
        db.SiteGeofences.Add(new SiteGeofence
        {
            Id = embedded.Id,
            Name = embedded.Name,
            NormalizedName = NormalizeName(embedded.Name),
            SiteId = linkedSite.Id,
            SiteNumber = linkedSite.ExternalCode,
            PolygonJson = PolygonAround((decimal)longitude, (decimal)latitude),
            Active = true
        });
        db.VehicleTrackingEvents.AddRange(
            Tracking("fallback-entry", now.AddMinutes(-35), latitude, longitude),
            Tracking("fallback-confirm", now.AddMinutes(-20), latitude, longitude),
            Tracking("fallback-exit", now.AddMinutes(-10), 0d, 0d));
        await db.SaveChangesAsync();

        var load = new Load
        {
            Id = loadId,
            Reference = "PLAN-20260827-L002",
            PlanningDate = planningDate,
            Status = LoadStatus.InProgress,
            VehicleId = vehicleId,
            Stops =
            [
                new LoadStop
                {
                    Id = stopId,
                    LoadId = loadId,
                    Sequence = 1,
                    Name = "Selsey Despatch",
                    PlannedArrivalUtc = now.AddMinutes(-30)
                }
            ]
        };

        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, [load], CancellationToken.None);

        var visit = Assert.Single(snapshot.Visits);
        Assert.Equal(loadId, visit.LoadId);
        Assert.Equal(stopId, visit.LoadStopId);
        Assert.NotNull(visit.ConfirmedAtUtc);
        Assert.NotNull(visit.ExitedAtUtc);
    }

    [Fact]
    public async Task Active_sql_catalogue_keeps_current_reconstructed_visit_when_durable_link_is_not_ready()
    {
        var fenceId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var fence = new EmbeddedFence(
            fenceId,
            "Current SQL Fence",
            null,
            null,
            null,
            0,
            0,
            null,
            [new GeoPoint(-1.01, 50.99), new GeoPoint(-0.99, 50.99), new GeoPoint(-0.99, 51.01), new GeoPoint(-1.01, 51.01)]);
        var visit = new DerivedVisit
        {
            Id = Guid.NewGuid(),
            VehicleId = vehicleId,
            VehicleIdentifier = "AB12CDE",
            Fence = fence,
            LoadId = loadId,
            LoadStopId = stopId,
            EnteredAtUtc = now.AddMinutes(-25),
            ConfirmedAtUtc = now.AddMinutes(-15),
            ExitedAtUtc = now.AddMinutes(-5),
            LastInsideAtUtc = now.AddMinutes(-6),
            DwellMinutes = 20
        };
        var snapshot = new EmbeddedGeofenceSnapshot([fence], [visit], [], [visit], 3, now.AddMinutes(-5));
        var load = new Load
        {
            Id = loadId,
            Reference = "PLAN-20260827-L002",
            PlanningDate = UkDate(now),
            Status = LoadStatus.InProgress,
            VehicleId = vehicleId,
            Stops = [new LoadStop { Id = stopId, LoadId = loadId, Sequence = 1, Name = fence.Name }]
        };

        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        db.SiteGeofences.Add(new SiteGeofence
        {
            Id = fenceId,
            Name = fence.Name,
            NormalizedName = NormalizeName(fence.Name),
            PolygonJson = PolygonAround(-1m, 51m),
            Active = true
        });
        await db.SaveChangesAsync();

        var merged = await EmbeddedGeofenceEvidenceMerge.MergeDurableProjectionAsync(db, snapshot, [load], CancellationToken.None);

        var retained = Assert.Single(merged.Visits);
        Assert.Equal(loadId, retained.LoadId);
        Assert.Equal(stopId, retained.LoadStopId);
        Assert.NotNull(retained.ExitedAtUtc);
        Assert.Single(merged.ConfirmedVisits);
    }

    private static VehicleTrackingEvent Tracking(string id, DateTimeOffset at, double latitude, double longitude) => new()
    {
        ProviderName = "RoadTech Falcon",
        ProviderEventId = id,
        VehicleIdentifier = "SLH225",
        EventTimeUtc = at,
        Latitude = (decimal)latitude,
        Longitude = (decimal)longitude,
        RawPayload = "{}",
        MatchStatus = "Received"
    };

    private static string PolygonAround(decimal longitude, decimal latitude) =>
        $$"""[[{{longitude - 0.01m}},{{latitude - 0.01m}}],[{{longitude + 0.01m}},{{latitude - 0.01m}}],[{{longitude + 0.01m}},{{latitude + 0.01m}}],[{{longitude - 0.01m}},{{latitude + 0.01m}}]]""";

    private static string NormalizeName(string value) =>
        string.Join(' ', value.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static DateOnly UkDate(DateTimeOffset value)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}
