using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlannerResourceReconciliationTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;

    public PlannerResourceReconciliationTests(CustomWebFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Reconciliation_backfills_unique_normalized_master_data_match_without_overwriting_existing_resources()
    {
        var date = new DateOnly(2026, 8, 24);
        Guid driverId;
        Guid vehicleId;
        Guid trailerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var driver = new Driver { EmployeeNumber = "D001", DisplayName = "David Conrad", TachoName = "Conrad, David" };
            var vehicle = new Vehicle { Registration = "RKJ 123", Abbreviation = "RKJ" };
            var trailer = new Trailer { TrailerNumber = "73" };
            db.Drivers.Add(driver);
            db.Vehicles.Add(vehicle);
            db.Trailers.Add(trailer);
            await db.SaveChangesAsync();
            driverId = driver.Id;
            vehicleId = vehicle.Id;
            trailerId = trailer.Id;
        }

        var request = new PlannerPlanImportRequest("slh-planner-plan-v3-source-lines", date, [
            new PlannerPlanRunRequest(
                "2",
                "2",
                "AM",
                date,
                "David-Conrad",
                "RKJ",
                "73",
                null,
                true,
                "Matched",
                new PlannerPlanSourceRequest("Lyons collections 240826.xlsm", "Collection Plan"),
                [new PlannerPlanStopRequest(1, "NWF-Selsey", "Morrisons-Stockton", 4, "TEST-SEL-4", "Std", "04:30:00", "05:00:00", "18:00:00", 11)])
        ]);

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var imported = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var load = await db.Loads.SingleAsync(x => x.Reference == "PLAN-20260824-2");
            Assert.Null(load.DriverId);
            Assert.Equal(vehicleId, load.VehicleId);
            Assert.Equal(trailerId, load.TrailerId);
            Assert.Equal(LoadStatus.Draft, load.Status);
        }

        var response = await client.PostAsync("/api/v1/planning/reconcile-resources/2026-08-24", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var load = await db.Loads.SingleAsync(x => x.Reference == "PLAN-20260824-2");
            Assert.Equal(driverId, load.DriverId);
            Assert.Equal(vehicleId, load.VehicleId);
            Assert.Equal(trailerId, load.TrailerId);
            Assert.Equal(LoadStatus.Planned, load.Status);
        }
    }

    [Fact]
    public async Task Reconciliation_does_not_guess_when_normalized_driver_match_is_ambiguous()
    {
        var date = new DateOnly(2026, 8, 24);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Drivers.AddRange(
                new Driver { EmployeeNumber = "D010", DisplayName = "A B" },
                new Driver { EmployeeNumber = "D011", DisplayName = "A-B" });
            db.Vehicles.Add(new Vehicle { Registration = "RKJ 123", Abbreviation = "RKJ" });
            await db.SaveChangesAsync();
        }

        var request = new PlannerPlanImportRequest("slh-planner-plan-v3-source-lines", date, [
            new PlannerPlanRunRequest(
                "9", "9", "AM", date, "AB", "RKJ", null, null, true, "Matched",
                new PlannerPlanSourceRequest("Lyons collections 240826.xlsm", "Collection Plan"),
                [new PlannerPlanStopRequest(1, "A", "B", 1, "TEST", "Std", "04:00:00", "05:00:00", "12:00:00", 1)])
        ]);

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/planning/import-plan", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/planning/reconcile-resources/2026-08-24", null)).StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var load = await verifyDb.Loads.SingleAsync(x => x.Reference == "PLAN-20260824-9");
        Assert.Null(load.DriverId);
        Assert.Equal(LoadStatus.Draft, load.Status);
    }
}
