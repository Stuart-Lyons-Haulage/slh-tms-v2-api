using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class FreshRoadTechGeofenceProjectionTests
{
    [Fact]
    public async Task Fresh_live_observation_is_projected_as_departure_for_shared_wallboard_state()
    {
        var fence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(x => x.Name.Trim() == "Swindon (Aldi)"));
        var longitude = fence.Points.Average(x => x.Longitude);
        var latitude = fence.Points.Average(x => x.Latitude);
        var vehicleId = Guid.NewGuid();
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var planningDate = UkDate(now);

        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);

        db.Vehicles.Add(new Vehicle { Id = vehicleId, Registration = "AB12CDE", Active = true });
        db.VehicleTrackingEvents.Add(new VehicleTrackingEvent
        {
            ProviderName = "RoadTech Falcon",
            ProviderEventId = "entry",
            VehicleIdentifier = "AB12CDE",
            EventTimeUtc = now.AddMinutes(-12),
            Latitude = (decimal)latitude,
            Longitude = (decimal)longitude,
            RawPayload = "{}",
            MatchStatus = "Received"
        });
        db.VehicleLiveStatuses.Add(new VehicleLiveStatus
        {
            VehicleIdentifier = "AB12CDE",
            LastEventTimeUtc = now,
            LastReceivedAtUtc = now,
            Latitude = 0m,
            Longitude = 0m,
            LastKnownStatus = "Moving"
        });

        db.SiteGeofences.Add(new SiteGeofence
        {
            Id = fence.Id,
            Name = fence.Name,
            NormalizedName = Normalize(fence.Name),
            PolygonJson = "[]",
            Active = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();

        var load = new Load
        {
            Id = loadId,
            Reference = "TEST-FRESH-PROJECTION",
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
                    Name = "Aldi Swindon",
                    PlannedArrivalUtc = now.AddMinutes(-15)
                }
            ]
        };

        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, [load], CancellationToken.None);
        var visit = Assert.Single(snapshot.Visits);
        Assert.Equal(loadId, visit.LoadId);
        Assert.Equal(stopId, visit.LoadStopId);
        Assert.NotNull(visit.ExitedAtUtc);
        Assert.NotNull(visit.ConfirmedAtUtc);

        await EmbeddedGeofenceSqlProjection.PersistAsync(db, snapshot, CancellationToken.None);

        var projected = Assert.Single(await db.GeofenceVisits.AsNoTracking().ToListAsync());
        Assert.Equal(loadId, projected.LoadId);
        Assert.Equal(stopId, projected.LoadStopId);
        Assert.NotNull(projected.ExitedAtUtc);
        Assert.Equal("Departed", projected.Status);
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

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
