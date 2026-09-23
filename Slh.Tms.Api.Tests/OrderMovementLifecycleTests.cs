using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderMovementLifecycleTests
{
    [Fact]
    public async Task Later_attachment_revision_enriches_same_movement_and_retains_source_lines()
    {
        await using var db = CreateDb();
        var service = new StagingService(db);
        var first = Stage("first", 1, "MSG-1", "po.xlsx", "Awaiting load/details");
        var second = Stage("second", 2, "MSG-2", "southbound.xlsx", "Planner ready");
        db.StagedImports.AddRange(first, second);
        await db.SaveChangesAsync();

        await service.ReviewAndPromote(first.Id, true, "Early PO checked", Planner(), CancellationToken.None);
        await service.ReviewAndPromote(second.Id, true, "Load detail checked", Planner(), CancellationToken.None);

        var movement = Assert.Single(await db.OrderMovements.ToListAsync());
        Assert.Equal("COOP:PO1001", movement.StableMovementKey);
        Assert.Equal(OrderMovementStatus.PlannerReady, movement.LifecycleStatus);

        var revisions = await db.OrderRevisions.OrderBy(x => x.RevisionNumber).ToListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Equal(first.Id, revisions[0].StagedImportId);
        Assert.Equal(revisions[0].Id, revisions[1].SupersedesRevisionId);
        Assert.Equal("southbound.xlsx", revisions[1].AttachmentIdentity);

        var lines = await db.OrderSourceLines.Where(x => x.RevisionId == revisions[1].Id).OrderBy(x => x.SourceRowKey).ToListAsync();
        Assert.Equal(new[] { "row-1", "row-2" }, lines.Select(x => x.SourceRowKey));
        Assert.Equal(new int?[] { 12, 8 }, lines.Select(x => x.Pallets));

        var order = Assert.Single(await db.TransportOrders.ToListAsync());
        Assert.Equal(second.Id, order.SourceStagedImportId);
        Assert.Equal(movement.Id, order.SourceMovementId);
        Assert.Equal(revisions[1].Id, movement.CurrentRevisionId);
    }

    [Fact]
    public async Task Later_approved_revision_updates_existing_live_order_dates()
    {
        await using var db = CreateDb();
        var service = new StagingService(db);
        var first = StageSingle("amend-first", "PO-AMEND-1", "2026-08-25", "2026-08-25", 4);
        var second = StageSingle("amend-second", "PO-AMEND-1", "2026-08-26", "2026-08-27", 9);
        db.StagedImports.AddRange(first, second);
        await db.SaveChangesAsync();

        await service.ReviewAndPromote(first.Id, true, "Original accepted", Planner(), CancellationToken.None);
        await service.ReviewAndPromote(second.Id, true, "Amendment accepted", Planner(), CancellationToken.None);

        var order = Assert.Single(await db.TransportOrders.ToListAsync());
        Assert.Equal("PO-AMEND-1", order.Reference);
        Assert.Equal(new DateOnly(2026, 8, 26), order.CollectionDate);
        Assert.Equal(new DateOnly(2026, 8, 27), order.DeliveryDate);
        Assert.Equal(9, order.Pallets);
        Assert.Equal(second.Id, order.SourceStagedImportId);
    }

    private static StagedImport Stage(string key, int lineCount, string messageId, string attachment, string state)
    {
        var lines = new[]
        {
            new { sourceRowKey = "row-1", collectionSite = "Groves Farm", deliverySite = "COOP Andover", collectionDate = "2026-08-24", deliveryDate = "2026-08-25", pallets = 12, palletType = "Standard" },
            new { sourceRowKey = "row-2", collectionSite = "Hill Farm", deliverySite = "COOP Andover", collectionDate = "2026-08-24", deliveryDate = "2026-08-25", pallets = 8, palletType = "Euro" }
        }.Take(lineCount);
        return new StagedImport
        {
            EntityType = "order",
            IdempotencyKey = key,
            Source = "PowerAutomate/InfoMailbox",
            PayloadJson = JsonSerializer.Serialize(new
            {
                customerCode = "COOP",
                poNumber = "PO-1001",
                collectionDate = "2026-08-24",
                deliveryDate = "2026-08-25",
                pallets = lineCount == 1 ? 12 : 20,
                messageId,
                attachmentIdentity = attachment,
                parserTemplate = "TSBC/COOP",
                parserVersion = "2",
                lifecycleStatus = state,
                sourceLines = lines
            })
        };
    }

    private static StagedImport StageSingle(string key, string reference, string collectionDate, string deliveryDate, int pallets) => new()
    {
        EntityType = "order",
        IdempotencyKey = key,
        Source = "PowerAutomate/InfoMailbox",
        PayloadJson = JsonSerializer.Serialize(new
        {
            customerCode = "AMAZON",
            poNumber = reference,
            collectionDate,
            deliveryDate,
            pallets,
            sellerName = "APS Produce",
            stallNumber = "Amazon delivery"
        })
    };

    private static ClaimsPrincipal Planner() => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, "planner@lyonshaulage.com") }, "test"));

    private static TmsDbContext CreateDb() => new(new DbContextOptionsBuilder<TmsDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
