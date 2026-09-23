using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlannerSiteReconciliationTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;

    public PlannerSiteReconciliationTests(CustomWebFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Reconciliation_applies_master_address_and_alias_without_copying_coordinates_or_losing_operational_detail()
    {
        var date = new DateOnly(2026, 8, 24);
        var loadId = Guid.NewGuid();
        var site = new Site
        {
            ExternalCode = "NWF-SELSEY",
            Name = "Natures Way Selsey",
            DriverTextName = "NWF Selsey",
            CollectionAddress = "Park Farm, Selsey, Chichester, PO20 0XY"
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.Add(site);
            db.Loads.Add(new Load
            {
                Id = loadId,
                Reference = "PLAN-20260824-C2",
                PlanningDate = date,
                Status = LoadStatus.Planned,
                Stops = [new LoadStop
                {
                    LoadId = loadId,
                    Sequence = 1,
                    Name = "Collect · NWF-Selsey",
                    Address = "to Morrisons-Stockton · 4 pallets · Ref TEST"
                }]
            });
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "masterdetail:site",
                IdempotencyKey = "masterdetail:site:nwf-selsey",
                PayloadJson = "{\"externalCode\":\"NWF-SELSEY\",\"aliases\":\"NWF-Selsey; Selsey NWF\",\"latitude\":50.7331,\"longitude\":-0.7891}",
                Status = StagingStatus.Promoted,
                Source = "test"
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var response = await client.PostAsync("/api/v1/planning/reconcile-sites/2026-08-24", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var stop = await verifyDb.LoadStops.SingleAsync(x => x.LoadId == loadId);
        Assert.StartsWith("Park Farm, Selsey, Chichester, PO20 0XY", stop.Address);
        Assert.Contains("4 pallets", stop.Address);
        Assert.Null(stop.Latitude);
        Assert.Null(stop.Longitude);
    }

    [Fact]
    public async Task Reconciliation_does_not_guess_ambiguous_alias()
    {
        var date = new DateOnly(2026, 8, 24);
        var loadId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.AddRange(
                new Site { ExternalCode = "A1", Name = "Depot One", DriverTextName = "Common Depot", CollectionAddress = "AA1 1AA" },
                new Site { ExternalCode = "A2", Name = "Depot Two", DriverTextName = "Common Depot", CollectionAddress = "BB2 2BB" });
            db.Loads.Add(new Load
            {
                Id = loadId,
                Reference = "PLAN-20260824-S1",
                PlanningDate = date,
                Status = LoadStatus.Planned,
                Stops = [new LoadStop { LoadId = loadId, Sequence = 1, Name = "Deliver · Common Depot", Address = "Ref TEST" }]
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/planning/reconcile-sites/2026-08-24", null)).StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var stop = await verifyDb.LoadStops.SingleAsync(x => x.LoadId == loadId);
        Assert.Equal("Ref TEST", stop.Address);
        Assert.Null(stop.Latitude);
        Assert.Null(stop.Longitude);
    }

    [Fact]
    public async Task Optional_master_detail_failure_does_not_turn_reconciliation_into_500()
    {
        var date = new DateOnly(2026, 8, 24);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            // Duplicate active external codes reproduce a legacy-data condition that makes
            // optional MasterDetailStore enrichment fail while core Site Master remains usable.
            db.Sites.AddRange(
                new Site { ExternalCode = "DUP", Name = "Depot One", CollectionAddress = "AA1 1AA" },
                new Site { ExternalCode = "DUP", Name = "Depot Two", CollectionAddress = "BB2 2BB" });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var response = await client.PostAsync("/api/v1/planning/reconcile-sites/2026-08-24", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
