using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmbeddedGeofenceDepartureReconstructionTests
{
    [Fact]
    public async Task Departure_reconstructs_stationary_dwell_and_completes_stop_without_repeated_provider_event()
    {
        var fence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(x => string.Equals(x.Name, "Runcton (Natures Way)", StringComparison.OrdinalIgnoreCase)));
        var longitude = fence.Points.Average(x => x.Longitude);
        var latitude = fence.Points.Average(x => x.Latitude);
        var now = DateTimeOffset.UtcNow;
        var planningDate = UkDate(now);
        var vehicleId = Guid.NewGuid();
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();

        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        db.Vehicles.Add(new Vehicle { Id = vehicleId, Registration = "AB12CDE", Active = true });
        db.VehicleTrackingEvents.AddRange(
            Tracking("entry", "AB12CDE", now.AddMinutes(-20), latitude, longitude),
            Tracking("exit", "AB12CDE", now.AddMinutes(-5), 0d, 0d));
        await db.SaveChangesAsync();

        var load = new Load
        {
            Id = loadId,
            Reference = "RUN-TEST",
            PlanningDate = planningDate,
            Status = LoadStatus.InProgress,
            VehicleId = vehicleId,
            Stops = [new LoadStop { Id = stopId, LoadId = loadId, Sequence = 1, Name = "NWF-Runcton", PlannedArrivalUtc = now.AddMinutes(-18) }]
        };

        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, GeofencePlanningMatch.PrepareLoads([load]), CancellationToken.None);
        var visit = Assert.Single(snapshot.Visits);

        Assert.NotNull(visit.ExitedAtUtc);
        Assert.NotNull(visit.ConfirmedAtUtc);
        Assert.True(visit.DwellMinutes >= 14);
        Assert.Equal(loadId, visit.LoadId);
        Assert.Equal(stopId, visit.LoadStopId);
        Assert.Contains(stopId, GeofencePlanningMatch.CompletedStopIds(load, snapshot.Visits));
    }

    private static VehicleTrackingEvent Tracking(string id, string vehicle, DateTimeOffset at, double latitude, double longitude) => new()
    {
        ProviderName = "RoadTech Falcon",
        ProviderEventId = id,
        VehicleIdentifier = vehicle,
        EventTimeUtc = at,
        Latitude = (decimal)latitude,
        Longitude = (decimal)longitude,
        RawPayload = "{}",
        MatchStatus = "Received"
    };

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
