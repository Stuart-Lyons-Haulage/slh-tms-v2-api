using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PalletPlanningControlTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public PalletPlanningControlTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Source_lines_preserve_provenance_and_partial_balance()
    {
        var seeded = await Seed();
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var allocate = await client.PostAsync("/api/v1/planning-control/allocations", Json(JsonSerializer.Serialize(new { orderId = seeded.OrderId, loadId = seeded.FirstLoadId, date = "2026-08-24", pallets = 7, note = "Split", sourceLineId = seeded.SourceLineId })));
        Assert.Equal(HttpStatusCode.OK, allocate.StatusCode);

        using var response = JsonDocument.Parse(await (await client.GetAsync("/api/v1/planning-control/pallets?date=2026-08-24")).Content.ReadAsStringAsync());
        var order = Assert.Single(response.RootElement.GetProperty("orders").EnumerateArray().Where(x => x.GetProperty("id").GetGuid() == seeded.OrderId));
        var line = Assert.Single(order.GetProperty("sourceLines").EnumerateArray());
        Assert.Equal(seeded.SourceLineId, line.GetProperty("sourceLineId").GetGuid());
        Assert.Equal("source-row-8", line.GetProperty("sourceRowKey").GetString());
        Assert.Equal(12, line.GetProperty("orderedPallets").GetInt32());
        Assert.Equal(7, line.GetProperty("plannedPallets").GetInt32());
        Assert.Equal(5, line.GetProperty("outstandingPallets").GetInt32());
        Assert.Equal("Standard", line.GetProperty("palletType").GetString());
    }

    [Fact]
    public async Task Allocation_above_source_line_quantity_is_rejected_without_mutation()
    {
        var seeded = await Seed();
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/planning-control/allocations", Json(JsonSerializer.Serialize(new { orderId = seeded.OrderId, loadId = seeded.FirstLoadId, date = "2026-08-24", pallets = 7, sourceLineId = seeded.SourceLineId })))).StatusCode);
        var rejected = await client.PostAsync("/api/v1/planning-control/allocations", Json(JsonSerializer.Serialize(new { orderId = seeded.OrderId, loadId = seeded.SecondLoadId, date = "2026-08-24", pallets = 6, sourceLineId = seeded.SourceLineId })));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.DoesNotContain(db.StagedImports, x => x.EntityType == "planningpalletallocation" && x.PayloadJson.Contains(seeded.SecondLoadId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Pending_review_order_payload_does_not_enrich_planner_orders()
    {
        var date = new DateOnly(2026, 8, 24);
        var reference = $"GH-{Guid.NewGuid():N}"[..18];
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.TransportOrders.Add(new TransportOrder
            {
                Reference = reference,
                CustomerCode = "GH",
                CollectionDate = date,
                Pallets = 4,
                SellerName = "Approved collection",
                StallNumber = "Approved destination"
            });
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "order",
                IdempotencyKey = $"pending-gh-{Guid.NewGuid():N}",
                Status = StagingStatus.PendingReview,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    poNumber = reference,
                    customerCode = "GH",
                    collectionDate = "2026-08-24",
                    collectionSite = "Waiting approval collection",
                    destination = "Waiting approval destination",
                    pallets = 99,
                    unitType = "Trays"
                })
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Read");
        using var response = JsonDocument.Parse(await (await client.GetAsync("/api/v1/planning-control/pallets?date=2026-08-24")).Content.ReadAsStringAsync());
        var order = Assert.Single(response.RootElement.GetProperty("orders").EnumerateArray().Where(x => x.GetProperty("reference").GetString() == reference));
        Assert.Equal("Approved collection", order.GetProperty("collection").GetString());
        Assert.Equal("Approved destination", order.GetProperty("destination").GetString());
        Assert.Equal("Approved destination", order.GetProperty("originalDestination").GetString());
        Assert.Equal("Approved collection", order.GetProperty("planningGroup").GetString());
        Assert.Equal(4, order.GetProperty("orderedPallets").GetInt32());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("palletType").ValueKind);
    }

    [Fact]
    public async Task Pending_review_register_order_does_not_appear_in_orders_to_plan()
    {
        var date = new DateOnly(2026, 9, 6);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "order",
                IdempotencyKey = $"pending-register-{Guid.NewGuid():N}",
                Status = StagingStatus.PendingReview,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    poNumber = "PENDING-REGISTER-ORDER",
                    customerCode = "IFCO",
                    collectionDate = date.ToString("yyyy-MM-dd"),
                    deliveryDate = date.ToString("yyyy-MM-dd"),
                    sellerName = "IFCO Glasshoughton",
                    stallNumber = "Runcton",
                    pallets = 26
                })
            });
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "order",
                IdempotencyKey = $"promoted-register-{Guid.NewGuid():N}",
                Status = StagingStatus.Promoted,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    poNumber = "PROMOTED-REGISTER-ORDER",
                    customerCode = "IFCO",
                    collectionDate = date.ToString("yyyy-MM-dd"),
                    deliveryDate = date.ToString("yyyy-MM-dd"),
                    sellerName = "IFCO Glasshoughton",
                    stallNumber = "Selsey",
                    pallets = 26
                })
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Read");
        using var response = JsonDocument.Parse(await (await client.GetAsync($"/api/v1/orders?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}")).Content.ReadAsStringAsync());
        var references = response.RootElement.EnumerateArray().Select(x => x.GetProperty("reference").GetString()).ToList();
        Assert.Contains("PROMOTED-REGISTER-ORDER", references);
        Assert.DoesNotContain("PENDING-REGISTER-ORDER", references);
    }

    [Fact]
    public async Task Run_stop_updates_preserve_planner_note()
    {
        var date = new DateOnly(2026, 9, 7);
        var load = new Load
        {
            Reference = ($"RUN-NOTE-{Guid.NewGuid():N}")[..20],
            PlanningDate = date,
            Status = LoadStatus.Draft
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Loads.Add(load);
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var update = await client.PutAsync($"/api/v1/runs/{load.Id}/stops", Json(JsonSerializer.Serialize(new[]
        {
            new { name = "Collect · Test", plannerNote = (string?)null },
            new { name = "Deliver · Test", plannerNote = "Ring gatehouse on arrival" }
        })));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using var response = JsonDocument.Parse(await (await client.GetAsync($"/api/v1/runs?date={date:yyyy-MM-dd}")).Content.ReadAsStringAsync());
        var savedLoad = Assert.Single(response.RootElement.EnumerateArray().Where(x => x.GetProperty("id").GetGuid() == load.Id));
        var delivery = Assert.Single(savedLoad.GetProperty("stops").EnumerateArray().Where(x => x.GetProperty("name").GetString() == "Deliver · Test"));
        Assert.Equal("Ring gatehouse on arrival", delivery.GetProperty("plannerNote").GetString());
    }

    private async Task<(Guid OrderId, Guid SourceLineId, Guid FirstLoadId, Guid SecondLoadId)> Seed()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var staged = new StagedImport { EntityType = "order", IdempotencyKey = $"pallet-{Guid.NewGuid():N}", Status = StagingStatus.Promoted, PayloadJson = """{"poNumber":"PO-PALLET","customerCode":"COOP","collectionDate":"2026-08-24","pallets":12}""" };
        var movement = new OrderMovement { CustomerCode = "COOP", StableMovementKey = $"COOP:PALLET:{Guid.NewGuid():N}", LifecycleStatus = OrderMovementStatus.PlannerReady };
        var revision = new OrderRevision { MovementId = movement.Id, StagedImportId = staged.Id, RevisionNumber = 1, PayloadJson = staged.PayloadJson };
        movement.CurrentRevisionId = revision.Id;
        var sourceLine = new OrderSourceLine { RevisionId = revision.Id, SourceRowKey = "source-row-8", CollectionSite = "Farm A", DeliverySite = "COOP Andover", CollectionDate = new DateOnly(2026, 8, 24), DeliveryDate = new DateOnly(2026, 8, 25), Pallets = 12, PalletType = "Standard", PayloadJson = "{}" };
        var order = new TransportOrder { SourceStagedImportId = staged.Id, SourceMovementId = movement.Id, Reference = ($"PO-{Guid.NewGuid():N}")[..20], CustomerCode = "COOP", CollectionDate = new DateOnly(2026, 8, 24), DeliveryDate = new DateOnly(2026, 8, 25), Pallets = 12 };
        var first = new Load { Reference = ($"RUN-{Guid.NewGuid():N}")[..20], PlanningDate = new DateOnly(2026, 8, 24) };
        var second = new Load { Reference = ($"RUN-{Guid.NewGuid():N}")[..20], PlanningDate = new DateOnly(2026, 8, 24) };
        db.AddRange(staged, movement, revision, sourceLine, order, first, second);
        await db.SaveChangesAsync();
        return (order.Id, sourceLine.Id, first.Id, second.Id);
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}