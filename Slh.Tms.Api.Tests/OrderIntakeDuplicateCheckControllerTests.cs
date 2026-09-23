using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeDuplicateCheckControllerTests
{
    [Fact]
    public async Task Same_po_and_same_core_values_is_exact_duplicate()
    {
        await using var db = CreateDb();
        db.StagedImports.Add(new StagedImport
        {
            EntityType = "order",
            IdempotencyKey = "existing-1",
            PayloadJson = JsonSerializer.Serialize(new
            {
                customerCode = "ALDI",
                customerPo = "PO-12345",
                poNumber = "PO-12345/SWINDON",
                collectionDate = "2026-08-22",
                deliveryDate = "2026-08-22",
                sellerName = "SLH Depot",
                stallNumber = "Aldi Swindon",
                pallets = 20
            }),
            Source = "test"
        });
        await db.SaveChangesAsync();

        var controller = Controller(db);
        var result = await controller.Check(new OrderIntakeDuplicateCheckRequest(
            "ALDI", "PO12345", null, "PO-12345/SWINDON",
            new DateOnly(2026, 8, 22), new DateOnly(2026, 8, 22),
            "SLH Depot", "Aldi Swindon", 20), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Exact duplicate", json);
        Assert.Contains("staging", json);
    }

    [Fact]
    public async Task Same_po_with_changed_pallets_is_amendment_update()
    {
        await using var db = CreateDb();
        db.TransportOrders.Add(new TransportOrder
        {
            Reference = "PO-67890/DARLINGTON",
            CustomerCode = "MORRISONS",
            CollectionDate = new DateOnly(2026, 8, 22),
            DeliveryDate = new DateOnly(2026, 8, 23),
            SellerName = "NWF Merston",
            StallNumber = "Morrisons Darlington",
            Pallets = 18
        });
        await db.SaveChangesAsync();

        var controller = Controller(db);
        var result = await controller.Check(new OrderIntakeDuplicateCheckRequest(
            "Morrisons", "PO67890", null, "PO-67890/DARLINGTON",
            new DateOnly(2026, 8, 22), new DateOnly(2026, 8, 23),
            "NWF Merston", "Morrisons Darlington", 22), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Amendment/update", json);
        Assert.Contains("live-order", json);
    }

    [Fact]
    public async Task Different_order_with_weak_overlap_remains_new()
    {
        await using var db = CreateDb();
        db.TransportOrders.Add(new TransportOrder
        {
            Reference = "PO-11111",
            CustomerCode = "ALDI",
            CollectionDate = new DateOnly(2026, 8, 22),
            DeliveryDate = new DateOnly(2026, 8, 22),
            SellerName = "SLH Depot",
            StallNumber = "Aldi Swindon",
            Pallets = 10
        });
        await db.SaveChangesAsync();

        var controller = Controller(db);
        var result = await controller.Check(new OrderIntakeDuplicateCheckRequest(
            "WAITROSE", "PO99999", null, "PO99999/BRACKNELL",
            new DateOnly(2026, 8, 22), new DateOnly(2026, 8, 23),
            "NWF Selsey", "Waitrose Bracknell", 26), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("New order", json);
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TmsDbContext(options);
    }

    private static OrderIntakeDuplicateCheckController Controller(TmsDbContext db) =>
        new(db, NullLogger<OrderIntakeDuplicateCheckController>.Instance);
}
