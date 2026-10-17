using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RoadTechHistoryRepairTests
{
    [Fact]
    public async Task Historical_replay_repairs_existing_event_time_and_position()
    {
        await using var db = CreateDb();
        db.VehicleTrackingEvents.Add(new VehicleTrackingEvent
        {
            ProviderName = "RoadTech Falcon",
            ProviderEventId = "event-1",
            VehicleIdentifier = "AB12CDE",
            EventTimeUtc = new DateTimeOffset(2026, 8, 28, 18, 1, 0, TimeSpan.Zero),
            Latitude = 51.0m,
            Longitude = -1.0m,
            RawPayload = "old",
            MatchStatus = "Received"
        });
        await db.SaveChangesAsync();

        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        var corrected = Record("event-1", "AB12 CDE", new DateTimeOffset(2026, 8, 27, 12, 1, 0, TimeSpan.Zero), 50.7581m, -0.7794m, "corrected");

        await store.PersistAsync([corrected], CancellationToken.None, markAsLiveReceipt: false);

        var stored = await db.VehicleTrackingEvents.SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 12, 1, 0, TimeSpan.Zero), stored.EventTimeUtc);
        Assert.Equal(50.7581m, stored.Latitude);
        Assert.Equal(-0.7794m, stored.Longitude);
        Assert.Equal("corrected", stored.RawPayload);
    }

    [Fact]
    public async Task Current_snapshot_repairs_matching_future_event_and_live_status()
    {
        await using var db = CreateDb();
        var correctedTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        var poisonedTime = DateTimeOffset.UtcNow.AddHours(30);
        db.VehicleTrackingEvents.Add(new VehicleTrackingEvent
        {
            ProviderName = "RoadTech Falcon",
            ProviderEventId = "live-poisoned",
            VehicleIdentifier = "AB12CDE",
            EventTimeUtc = poisonedTime,
            Latitude = 51.0m,
            Longitude = -1.0m,
            RawPayload = "poisoned",
            MatchStatus = "Received"
        });
        db.VehicleLiveStatuses.Add(new VehicleLiveStatus
        {
            VehicleIdentifier = "AB12CDE",
            LastEventTimeUtc = poisonedTime,
            LastReceivedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            Latitude = 51.0m,
            Longitude = -1.0m
        });
        await db.SaveChangesAsync();

        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        var current = Record("live-poisoned", "AB12 CDE", correctedTime, 50.7581m, -0.7794m, "current");

        await store.PersistAsync([current], CancellationToken.None, markAsLiveReceipt: true);

        var stored = await db.VehicleTrackingEvents.SingleAsync();
        var live = await db.VehicleLiveStatuses.SingleAsync();
        Assert.Equal(correctedTime, stored.EventTimeUtc);
        Assert.Equal(correctedTime, live.LastEventTimeUtc);
        Assert.Equal(50.7581m, stored.Latitude);
        Assert.Equal(50.7581m, live.Latitude);
        Assert.Equal("current", stored.RawPayload);
    }

    [Fact]
    public async Task Current_snapshot_repairs_future_stored_trail_for_same_vehicle()
    {
        await using var db = CreateDb();
        var currentTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        var futureOne = currentTime.AddHours(29).AddMinutes(-30);
        var futureTwo = currentTime.AddHours(29);
        db.VehicleTrackingEvents.AddRange(
            new VehicleTrackingEvent
            {
                ProviderName = "RoadTech Falcon",
                ProviderEventId = "future-1",
                VehicleIdentifier = "AB12 CDE",
                EventTimeUtc = futureOne,
                Latitude = 51.0m,
                Longitude = -1.0m,
                RawPayload = "future-1",
                MatchStatus = "Received"
            },
            new VehicleTrackingEvent
            {
                ProviderName = "RoadTech Falcon",
                ProviderEventId = "future-2",
                VehicleIdentifier = "AB12CDE",
                EventTimeUtc = futureTwo,
                Latitude = 51.1m,
                Longitude = -1.1m,
                RawPayload = "future-2",
                MatchStatus = "Received"
            });
        await db.SaveChangesAsync();

        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        var current = Record("current", "AB12 CDE", currentTime, 50.7581m, -0.7794m, "current");

        await store.PersistAsync([current], CancellationToken.None, markAsLiveReceipt: true);

        var rows = await db.VehicleTrackingEvents.OrderBy(row => row.EventTimeUtc).ToListAsync();
        Assert.Equal(currentTime.AddMinutes(-30), rows[0].EventTimeUtc);
        Assert.Equal(currentTime, rows[1].EventTimeUtc);
        Assert.Contains("ClockRepaired", rows[0].MatchStatus);
        Assert.Contains("ClockRepaired", rows[1].MatchStatus);
    }

    [Fact]
    public async Task Current_snapshot_repairs_future_stored_trail_without_vehicle_anchor()
    {
        await using var db = CreateDb();
        var currentTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        var futureTime = currentTime.AddHours(29);
        db.VehicleTrackingEvents.Add(new VehicleTrackingEvent
        {
            ProviderName = "RoadTech Falcon",
            ProviderEventId = "future-unmatched",
            VehicleIdentifier = "UNKNOWN-FALCON-KEY",
            EventTimeUtc = futureTime,
            Latitude = 51.1m,
            Longitude = -1.1m,
            RawPayload = "future",
            MatchStatus = "Received"
        });
        await db.SaveChangesAsync();

        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        var current = Record("current", "AB12 CDE", currentTime, 50.7581m, -0.7794m, "current");

        await store.PersistAsync([current], CancellationToken.None, markAsLiveReceipt: true);

        var repaired = await db.VehicleTrackingEvents.SingleAsync(row => row.ProviderEventId == "future-unmatched");
        Assert.Equal(currentTime, repaired.EventTimeUtc);
        Assert.Contains("ClockRepaired", repaired.MatchStatus);
    }

    [Fact]
    public async Task Historical_replay_does_not_refresh_live_status()
    {
        await using var db = CreateDb();
        db.VehicleLiveStatuses.Add(new VehicleLiveStatus
        {
            VehicleIdentifier = "AB12CDE",
            LastEventTimeUtc = new DateTimeOffset(2026, 8, 27, 12, 30, 0, TimeSpan.Zero),
            LastReceivedAtUtc = new DateTimeOffset(2026, 8, 27, 12, 30, 0, TimeSpan.Zero),
            Latitude = 51.0m,
            Longitude = -1.0m
        });
        await db.SaveChangesAsync();

        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        var historical = Record("event-2", "AB12 CDE", new DateTimeOffset(2026, 8, 27, 8, 0, 0, TimeSpan.Zero), 50.0m, -0.5m, "historical");

        await store.PersistAsync([historical], CancellationToken.None, markAsLiveReceipt: false);

        var live = await db.VehicleLiveStatuses.SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 12, 30, 0, TimeSpan.Zero), live.LastEventTimeUtc);
        Assert.Equal(51.0m, live.Latitude);
        Assert.Equal(-1.0m, live.Longitude);
    }

    [Fact]
    public void Current_future_timestamp_is_normalised_to_receipt_time()
    {
        var receivedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        var future = Record("live-1", "AB12 CDE", new DateTimeOffset(2026, 8, 28, 18, 1, 0, TimeSpan.Zero), 50.1m, -0.6m, "live");

        var normalised = DotTrackingIngestionService.NormaliseCurrentEventTimes([future], receivedAt);

        Assert.Equal(receivedAt, Assert.Single(normalised).EventTimeUtc);
    }

    [Fact]
    public void Today_history_uses_current_vehicle_clock_to_remove_systematic_future_skew()
    {
        var now = new DateTimeOffset(2026, 8, 27, 12, 5, 0, TimeSpan.Zero);
        var current = new[]
        {
            Record("current", "AB12 CDE", new DateTimeOffset(2026, 8, 27, 12, 1, 0, TimeSpan.Zero), 50.2m, -0.7m, "current")
        };
        var history = new[]
        {
            Record("hist-1", "AB12 CDE", new DateTimeOffset(2026, 8, 28, 17, 31, 0, TimeSpan.Zero), 50.0m, -0.5m, "history-1"),
            Record("hist-2", "AB12 CDE", new DateTimeOffset(2026, 8, 28, 18, 1, 0, TimeSpan.Zero), 50.1m, -0.6m, "history-2")
        };

        var normalised = DotTrackingIngestionService.NormaliseHistoricalEventTimes(history, current, new DateOnly(2026, 8, 27), now);

        Assert.Equal(new DateTimeOffset(2026, 8, 27, 11, 31, 0, TimeSpan.Zero), normalised[0].EventTimeUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 12, 1, 0, TimeSpan.Zero), normalised[1].EventTimeUtc);
        Assert.Equal(TimeSpan.FromMinutes(30), normalised[1].EventTimeUtc - normalised[0].EventTimeUtc);
    }

    [Fact]
    public void Legitimate_today_history_is_not_shifted()
    {
        var now = new DateTimeOffset(2026, 8, 27, 12, 5, 0, TimeSpan.Zero);
        var current = new[]
        {
            Record("current", "AB12 CDE", new DateTimeOffset(2026, 8, 27, 12, 1, 0, TimeSpan.Zero), 50.2m, -0.7m, "current")
        };
        var history = new[]
        {
            Record("hist", "AB12 CDE", new DateTimeOffset(2026, 8, 27, 11, 45, 0, TimeSpan.Zero), 50.0m, -0.5m, "history")
        };

        var normalised = DotTrackingIngestionService.NormaliseHistoricalEventTimes(history, current, new DateOnly(2026, 8, 27), now);

        Assert.Equal(history[0].EventTimeUtc, Assert.Single(normalised).EventTimeUtc);
    }

    private static DotTelemetryRecord Record(string id, string vehicle, DateTimeOffset time, decimal latitude, decimal longitude, string payload) =>
        new(id, vehicle, time, latitude, longitude, 22m, true, true, "Received", payload);

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"roadtech-history-repair-{Guid.NewGuid()}")
            .Options;
        return new TmsDbContext(options);
    }
}
