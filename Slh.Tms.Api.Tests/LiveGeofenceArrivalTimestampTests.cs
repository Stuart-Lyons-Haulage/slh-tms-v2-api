using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class LiveGeofenceArrivalTimestampTests
{
    [Fact]
    public async Task Fresh_receipt_extends_dwell_without_replacing_provider_entry_time()
    {
        var fence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(x => x.Name.Trim() == "Swindon (Aldi)"));
        var longitude = fence.Points.Average(x => x.Longitude);
        var latitude = fence.Points.Average(x => x.Latitude);
        var vehicleId = Guid.NewGuid();
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var enteredAt = now.AddMinutes(-12);
        var planningDate = UkDate(now);

        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        db.Vehicles.Add(new Vehicle { Id = vehicleId, Registration = "AB12CDE", Active = true });
        db.VehicleTrackingEvents.Add(new VehicleTrackingEvent
        {
            ProviderName = "RoadTech Falcon",
            ProviderEventId = "entry",
            VehicleIdentifier = "AB12CDE",
            EventTimeUtc = enteredAt,
            Latitude = (decimal)latitude,
            Longitude = (decimal)longitude,
            RawPayload = "{}",
            MatchStatus = "Received"
        });
        db.VehicleLiveStatuses.Add(new VehicleLiveStatus
        {
            VehicleIdentifier = "AB12CDE",
            LastEventTimeUtc = enteredAt,
            LastReceivedAtUtc = now,
            Latitude = (decimal)latitude,
            Longitude = (decimal)longitude,
            LastKnownStatus = "Received"
        });
        await db.SaveChangesAsync();

        var load = new Load
        {
            Id = loadId,
            Reference = "TEST-ARRIVAL",
            PlanningDate = planningDate,
            Status = LoadStatus.InProgress,
            VehicleId = vehicleId,
            Stops = [new LoadStop { Id = stopId, LoadId = loadId, Sequence = 1, Name = "Aldi Swindon", PlannedArrivalUtc = enteredAt }]
        };

        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, [load], CancellationToken.None);
        var visit = Assert.Single(snapshot.Visits);

        Assert.Equal(enteredAt, visit.EnteredAtUtc);
        Assert.True(visit.LastInsideAtUtc > visit.EnteredAtUtc);
        Assert.True(visit.DwellMinutes >= 10);
        Assert.NotNull(visit.ConfirmedAtUtc);
    }

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
