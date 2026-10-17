using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlannerResourcePropagationTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public PlannerResourcePropagationTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Idempotent_reimport_reapplies_resolved_resources_to_existing_load()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Database.EnsureDeleted();
            db.Database.EnsureCreated();
            db.Drivers.Add(new Driver { EmployeeNumber = "D1", DisplayName = "Driver One", Active = true });
            db.Vehicles.Add(new Vehicle { Registration = "AB12CDE", Abbreviation = "ABC", Active = true });
            db.Trailers.Add(new Trailer { TrailerNumber = "22", Active = true, StandardCapacity = 26, EuroCapacity = 33 });
            await db.SaveChangesAsync();
        }

        var date = new DateOnly(2026, 8, 24);
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var request = new PlannerPlanImportRequest("slh-planner-plan-v3-source-lines", date,
        [
            new PlannerPlanRunRequest("C1", "1", "AM", date, "Driver One", "ABC", "22", null, true, "Matched",
                new PlannerPlanSourceRequest("plan.xlsm", "Collection Plan"),
                [new PlannerPlanStopRequest(1, "Collection", "Delivery", 10, "R1", "Std", "08:00:00", null, "10:00:00", 1)])
        ]);

        var first = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var load = db.Loads.Single(x => x.Reference == "PLAN-20260824-C1");
            load.DriverId = null;
            load.VehicleId = null;
            load.TrailerId = null;
            load.Status = LoadStatus.Draft;
            await db.SaveChangesAsync();
        }

        var second = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var repaired = finalDb.Loads.Single(x => x.Reference == "PLAN-20260824-C1");
        Assert.NotNull(repaired.DriverId);
        Assert.NotNull(repaired.VehicleId);
        Assert.NotNull(repaired.TrailerId);
        Assert.Equal(LoadStatus.Planned, repaired.Status);
    }
}
