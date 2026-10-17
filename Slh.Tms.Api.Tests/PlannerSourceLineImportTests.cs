using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlannerSourceLineImportTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;

    public PlannerSourceLineImportTests(CustomWebFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Run_3_preserves_each_collection_line_and_destination_provenance()
    {
        var date = new DateOnly(2026, 8, 21);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.TransportOrders.AddRange(
                Order("STK-SEL-4", date, 4, "NWF-Selsey", "Morrisons-Stockton"),
                Order("STK-MER-5", date, 5, "NWF-Merston", "Morrisons-Stockton"),
                Order("STK-RUN-5", date, 5, "NWF-Runcton", "Morrisons-Stockton"),
                Order("WAK-RUN-10", date, 10, "NWF-Runcton", "Morrisons-Wakefield"));
            await db.SaveChangesAsync();
        }

        var request = new PlannerPlanImportRequest("slh-planner-plan-v3-source-lines", date, [
            new PlannerPlanRunRequest("3", "3", "AM", date, null, null, null, null, true, "Matched",
                new PlannerPlanSourceRequest("Lyons collections 210826.xlsm", "Collection Plan"), [
                    Stop(1, 11, "NWF-Selsey", "Morrisons-Stockton", 4, "STK-SEL-4", "04:30:00", "05:00:00"),
                    Stop(2, 12, "NWF-Merston", "Morrisons-Stockton", 5, "STK-MER-5", "05:00:00", "05:30:00"),
                    Stop(3, 13, "NWF-Runcton", "Morrisons-Stockton", 5, "STK-RUN-5", "05:30:00", "06:15:00"),
                    Stop(4, 14, "NWF-Runcton", "Morrisons-Wakefield", 10, "WAK-RUN-10", "05:30:00", "06:15:00")
                ])
        ]);

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var response = await client.PostAsJsonAsync("/api/v1/planning/import-source-plan", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var load = finalDb.Loads.Single(row => row.Reference == "PLAN-20260821-3");
        var stops = finalDb.LoadStops.Where(row => row.LoadId == load.Id).OrderBy(row => row.Sequence).ToList();

        Assert.Equal(6, stops.Count);
        Assert.Equal(new[]
        {
            "Collect · NWF-Selsey",
            "Collect · NWF-Merston",
            "Collect · NWF-Runcton",
            "Collect · NWF-Runcton",
            "Deliver · Morrisons-Stockton",
            "Deliver · Morrisons-Wakefield"
        }, stops.Select(row => row.Name));
        Assert.Contains("14 pallets total", stops[4].Address);
        Assert.Contains("4 from NWF-Selsey", stops[4].Address);
        Assert.Contains("5 from NWF-Merston", stops[4].Address);
        Assert.Contains("5 from NWF-Runcton", stops[4].Address);
        Assert.Contains("10 pallets total", stops[5].Address);
        Assert.Contains("10 from NWF-Runcton", stops[5].Address);

        var allocations = finalDb.StagedImports.Where(row => row.EntityType == PlanningAllocationStore.EntityType).ToList();
        var total = allocations.Sum(row => JsonSerializer.Deserialize<AllocationState>(row.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Pallets);
        Assert.Equal(24, total);
    }

    [Fact]
    public async Task Source_import_matches_canonical_trailer_and_site_without_losing_driver_line()
    {
        var date = new DateOnly(2026, 8, 26);
        var selseyFence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(fence =>
            fence.Name.Contains("Selsey", StringComparison.OrdinalIgnoreCase) &&
            fence.Name.Contains("Natures Way", StringComparison.OrdinalIgnoreCase)));
        var longitude = (decimal)selseyFence.Points.Average(point => point.Longitude);
        var latitude = (decimal)selseyFence.Points.Average(point => point.Latitude);
        Guid canonicalTrailerId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var siteId = Guid.NewGuid();
            canonicalTrailerId = Guid.NewGuid();
            db.Drivers.Add(new Driver { EmployeeNumber = "T001", DisplayName = "Test Driver", Active = true });
            db.Vehicles.Add(new Vehicle { Registration = "AB12ABC", Abbreviation = "ABC", Active = true });
            db.Trailers.Add(new Trailer { Id = canonicalTrailerId, TrailerNumber = "SLH2", Active = true });
            db.Sites.Add(new Site { Id = siteId, ExternalCode = "NWF-SEL", Name = "Selsey Nature's Way", DriverTextName = "NWF-Selsey", Active = true });
            db.SiteGeofences.Add(new SiteGeofence
            {
                Name = selseyFence.Name,
                NormalizedName = selseyFence.Name.Trim().ToUpperInvariant(),
                SiteId = siteId,
                SiteNumber = "NWF-SEL",
                PolygonJson = "[]",
                Active = true
            });
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "masterdetail:site",
                IdempotencyKey = "masterdetail:site:nwfsel",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    externalCode = "NWF-SEL",
                    aliases = "NWF-Selsey|Selsey (Natures Way)",
                    latitude,
                    longitude
                }),
                Status = StagingStatus.Promoted,
                Source = "test"
            });
            db.TransportOrders.Add(Order("SEL-10", date, 10, "NWF-Selsey", "Morrisons-Stockton"));
            await db.SaveChangesAsync();
        }

        var request = new PlannerPlanImportRequest("slh-planner-plan-v3-source-lines", date, [
            new PlannerPlanRunRequest("TEST-1", "1", "AM", date, "Test Driver", "ABC", "02", null, true, "Matched",
                new PlannerPlanSourceRequest("Lyons collections 260826.xlsm", "Collection Plan"), [
                    Stop(1, 2, "NWF-Selsey", "Morrisons-Stockton", 10, "SEL-10", "05:00:00", "05:30:00")
                ])
        ]);

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var response = await client.PostAsJsonAsync("/api/v1/planning/import-source-plan", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<PlannerPlanImportSummary>();
        Assert.NotNull(summary);
        Assert.Empty(summary!.UnresolvedDrivers);
        Assert.Empty(summary.UnresolvedVehicles);
        Assert.Empty(summary.UnresolvedTrailers);
        Assert.DoesNotContain(summary.Warnings, warning =>
            warning.Contains("NWF-Selsey", StringComparison.OrdinalIgnoreCase) &&
            (warning.Contains("did not resolve uniquely", StringComparison.OrdinalIgnoreCase) ||
             warning.Contains("no active linked geofence", StringComparison.OrdinalIgnoreCase)));

        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var load = finalDb.Loads.Single(row => row.Reference == "PLAN-20260826-TEST-1");
        Assert.Equal(canonicalTrailerId, load.TrailerId);
        Assert.Equal(LoadStatus.Planned, load.Status);

        var stop = finalDb.LoadStops.Where(row => row.LoadId == load.Id).OrderBy(row => row.Sequence).First();
        Assert.Equal("Collect · NWF-Selsey", stop.Name);
        Assert.Contains("10 pallets", stop.Address);
        Assert.Contains("for Morrisons-Stockton", stop.Address);
        Assert.Equal(latitude, stop.Latitude);
        Assert.Equal(longitude, stop.Longitude);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 4, 0, 0, TimeSpan.Zero), stop.PlannedArrivalUtc);
    }

    private static TransportOrder Order(string reference, DateOnly date, int pallets, string collection, string delivery) =>
        new() { Reference = reference, CustomerCode = "TEST", CollectionDate = date, Pallets = pallets, SellerName = collection, StallNumber = delivery };

    private static PlannerPlanStopRequest Stop(int sequence, int sourceRow, string collection, string delivery, int pallets, string reference, string collectFrom, string collectTo) =>
        new(sequence, collection, delivery, pallets, reference, "Std", collectFrom, collectTo, "18:00:00", sourceRow);

    private sealed record AllocationState(Guid OrderId, Guid LoadId, int Pallets, DateOnly Date, DateTimeOffset UpdatedAtUtc, string? UpdatedBy);
}
