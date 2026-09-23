using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PreDispatchSafetyTests
{
    private static readonly DateTimeOffset EvidenceAt = new(2026, 8, 28, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Missing_trailer_is_unverified_and_never_mutates_run_status()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        var driver = Driver("D1");
        var vehicle = Vehicle("SLH1");
        var load = LoadFor(new DateOnly(2026, 8, 28), driver.Id, vehicle.Id, trailerId: null);
        db.Drivers.Add(driver);
        db.Vehicles.Add(vehicle);
        db.Loads.Add(load);
        await db.SaveChangesAsync();
        await SaveCapacity(db, load, 18, 26);

        var readiness = await new PreDispatchSafetyService(db, new FixedTimeProvider(EvidenceAt))
            .EvaluateAsync(load.Id, CancellationToken.None);

        Assert.Equal("Unverified", readiness.Classification);
        Assert.True(readiness.RequiresAcknowledgement);
        Assert.Contains(readiness.Checks, item => item.Code == "TrailerAllocated" && !item.Passed && item.Severity == "Warning");
        Assert.Equal(LoadStatus.Planned, (await db.Loads.SingleAsync()).Status);
    }

    [Fact]
    public async Task Capacity_overrun_requires_planner_acknowledgement_instead_of_hard_blocking_dispatch()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        var driver = Driver("D2");
        var vehicle = Vehicle("SLH2");
        var trailer = new Trailer { TrailerNumber = "SLH20", Active = true };
        var load = LoadFor(new DateOnly(2026, 8, 28), driver.Id, vehicle.Id, trailer.Id);
        db.Drivers.Add(driver);
        db.Vehicles.Add(vehicle);
        db.Trailers.Add(trailer);
        db.Loads.Add(load);
        await db.SaveChangesAsync();
        await SaveCapacity(db, load, 27, 26);

        var readiness = await new PreDispatchSafetyService(db, new FixedTimeProvider(EvidenceAt))
            .EvaluateAsync(load.Id, CancellationToken.None);

        Assert.Equal("Unverified", readiness.Classification);
        Assert.True(readiness.RequiresAcknowledgement);
        Assert.Contains(readiness.Checks, item => item.Code == "CapacityWithinLimit" && !item.Passed && item.Severity == "Warning");
        Assert.Equal(LoadStatus.Planned, (await db.Loads.SingleAsync()).Status);
    }

    [Fact]
    public async Task Positive_load_against_zero_recorded_capacity_remains_blocked()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        var driver = Driver("D2-ZERO");
        var vehicle = Vehicle("SLH2Z");
        var trailer = new Trailer { TrailerNumber = "SLH20Z", Active = true };
        var load = LoadFor(new DateOnly(2026, 8, 28), driver.Id, vehicle.Id, trailer.Id);
        db.Drivers.Add(driver);
        db.Vehicles.Add(vehicle);
        db.Trailers.Add(trailer);
        db.Loads.Add(load);
        await db.SaveChangesAsync();
        await SaveCapacity(db, load, 1, 0);

        var readiness = await new PreDispatchSafetyService(db, new FixedTimeProvider(EvidenceAt))
            .EvaluateAsync(load.Id, CancellationToken.None);

        Assert.Equal("Blocked", readiness.Classification);
        Assert.Contains(readiness.Checks, item => item.Code == "CapacityWithinLimit" && !item.Passed && item.Severity == "Critical");
    }

    [Fact]
    public async Task Overlapping_same_driver_assignment_is_blocked()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        var date = new DateOnly(2026, 8, 28);
        var driver = Driver("D3");
        var vehicle = Vehicle("SLH3");
        var otherVehicle = Vehicle("SLH4");
        var trailer = new Trailer { TrailerNumber = "SLH21", Active = true };
        var otherTrailer = new Trailer { TrailerNumber = "SLH22", Active = true };
        var target = LoadFor(date, driver.Id, vehicle.Id, trailer.Id, startUtc: EvidenceAt.AddHours(2), endUtc: EvidenceAt.AddHours(6));
        var existing = LoadFor(date, driver.Id, otherVehicle.Id, otherTrailer.Id, startUtc: EvidenceAt.AddHours(4), endUtc: EvidenceAt.AddHours(8));
        existing.Reference = "EXISTING-02";
        db.Drivers.Add(driver);
        db.Vehicles.AddRange(vehicle, otherVehicle);
        db.Trailers.AddRange(trailer, otherTrailer);
        db.Loads.AddRange(target, existing);
        await db.SaveChangesAsync();
        await SaveCapacity(db, target, 18, 26);

        var readiness = await new PreDispatchSafetyService(db, new FixedTimeProvider(EvidenceAt))
            .EvaluateAsync(target.Id, CancellationToken.None);

        Assert.Equal("Blocked", readiness.Classification);
        Assert.Contains(readiness.Checks, item => item.Code == "DriverConflict" && !item.Passed && item.Severity == "Critical");
        Assert.Equal(LoadStatus.Planned, (await db.Loads.SingleAsync(item => item.Id == target.Id)).Status);
    }

    [Fact]
    public async Task Non_overlapping_timed_resource_reuse_remains_recommended()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        var date = new DateOnly(2026, 8, 28);
        var driver = Driver("D4");
        var vehicle = Vehicle("SLH5");
        var otherVehicle = Vehicle("SLH6");
        var trailer = new Trailer { TrailerNumber = "SLH23", Active = true };
        var otherTrailer = new Trailer { TrailerNumber = "SLH24", Active = true };
        var target = LoadFor(date, driver.Id, vehicle.Id, trailer.Id, startUtc: EvidenceAt.AddHours(2), endUtc: EvidenceAt.AddHours(5));
        var existing = LoadFor(date, driver.Id, otherVehicle.Id, otherTrailer.Id, startUtc: EvidenceAt.AddHours(6), endUtc: EvidenceAt.AddHours(9));
        existing.Reference = "EXISTING-03";
        db.Drivers.Add(driver);
        db.Vehicles.AddRange(vehicle, otherVehicle);
        db.Trailers.AddRange(trailer, otherTrailer);
        db.Loads.AddRange(target, existing);
        await db.SaveChangesAsync();
        await SaveCapacity(db, target, 18, 26);

        var readiness = await new PreDispatchSafetyService(db, new FixedTimeProvider(EvidenceAt))
            .EvaluateAsync(target.Id, CancellationToken.None);

        Assert.Equal("Recommended", readiness.Classification);
        Assert.Contains(readiness.Checks, item => item.Code == "DriverConflict" && item.Passed);
    }

    private static async Task SaveCapacity(TmsDbContext db, Load load, decimal used, decimal total) =>
        await RunOperationalStore.SaveAsync(
            db,
            load,
            new RunOperationalValues(used, total, "Standard pallets", null, null, null),
            "test",
            CancellationToken.None);

    private static Driver Driver(string employee) => new()
    {
        EmployeeNumber = employee,
        DisplayName = $"Driver {employee}",
        Active = true
    };

    private static Vehicle Vehicle(string registration) => new()
    {
        Registration = registration,
        Active = true
    };

    private static Load LoadFor(
        DateOnly date,
        Guid driverId,
        Guid vehicleId,
        Guid? trailerId,
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null)
    {
        return new Load
        {
            Reference = $"RUN-{Guid.NewGuid():N}"[..20],
            PlanningDate = date,
            Status = LoadStatus.Planned,
            DriverId = driverId,
            VehicleId = vehicleId,
            TrailerId = trailerId,
            Stops =
            [
                new LoadStop { Sequence = 1, Name = "Collection", Latitude = 52.0m, Longitude = -1.0m, PlannedArrivalUtc = startUtc },
                new LoadStop { Sequence = 2, Name = "Delivery", Latitude = 52.1m, Longitude = -1.1m, PlannedArrivalUtc = endUtc }
            ]
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}