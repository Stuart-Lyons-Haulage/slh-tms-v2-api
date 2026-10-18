using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class WarehouseMovementTests
{
    [Fact]
    public async Task Depot_aliases_include_driver_and_planned_warehouse_arrival()
    {
        await using var db = CreateDb();
        var driver = new Driver { EmployeeNumber = "D1", DisplayName = "Adam Smith" };
        var site = new Site { ExternalCode = "SLH-FRV", Name = "SLH-Lyons Consolidation Centre FRV", Aliases = "Barnham Coldstore, Stuart Lyons Distribution" };
        var expected = new DateTimeOffset(2026, 8, 24, 18, 30, 0, TimeSpan.Zero);
        var load = new Load { Reference = "RUN 4 PM", PlanningDate = new DateOnly(2026, 8, 24), DriverId = driver.Id, Stops =
        [
            new() { LoadId = Guid.Empty, Sequence = 1, Name = "Deliver · Barnham Coldstore", PlannedArrivalUtc = expected }
        ] };
        foreach (var stop in load.Stops) stop.LoadId = load.Id;
        db.AddRange(driver, site, load);
        var inbound = AddLine(db, "Vitacress Runcton", "Barnham Coldstore", 17, "Standard", "WAITROSE-17");
        AddAllocation(db, load.Id, inbound.OrderId, inbound.LineId, 17);
        await db.SaveChangesAsync();

        var result = await new WarehouseMovementService(db).BuildDailyAsync(new DateOnly(2026, 8, 24), CancellationToken.None);

        var row = Assert.Single(result.Inbound);
        Assert.Equal("Adam Smith", row.Driver);
        Assert.Equal(expected, row.ExpectedAtUtc);
        Assert.Equal("RUN 4 PM", row.RunReference);
        Assert.Equal(17, row.PlannedPallets);
    }

    [Fact]
    public async Task Genuine_slh_stops_create_separate_inbound_and_outbound_rows_but_vehicle_bulking_does_not()
    {
        await using var db = CreateDb();
        var site = new Site { ExternalCode = "SLH-FRV", Name = "SLH-Lyons Consolidation Centre FRV" };
        var load = new Load { Reference = "MON-PM-01", PlanningDate = new DateOnly(2026, 8, 24), Stops =
        [
            new() { LoadId = Guid.Empty, Sequence = 1, Name = "Collect · Farm A" },
            new() { LoadId = Guid.Empty, Sequence = 2, Name = "Deliver · SLH-Lyons Consolidation Centre FRV" },
            new() { LoadId = Guid.Empty, Sequence = 3, Name = "Collect · SLH-Lyons Consolidation Centre FRV" },
            new() { LoadId = Guid.Empty, Sequence = 4, Name = "Deliver · Customer A" }
        ] };
        foreach (var stop in load.Stops) stop.LoadId = load.Id;
        db.AddRange(site, load);
        var inbound = AddLine(db, "Farm A", site.Name, 10, "Standard", "LOAD-IN");
        var outbound = AddLine(db, site.Name, "Customer A", 8, "Euro", "LOAD-OUT");
        var bulked = AddLine(db, "Farm B", "Customer B", 6, "Standard", "LOAD-BULK");
        AddAllocation(db, load.Id, inbound.OrderId, inbound.LineId, 10);
        AddAllocation(db, load.Id, outbound.OrderId, outbound.LineId, 8);
        AddAllocation(db, load.Id, bulked.OrderId, bulked.LineId, 6);
        await db.SaveChangesAsync();

        var result = await new WarehouseMovementService(db).BuildDailyAsync(new DateOnly(2026, 8, 24), CancellationToken.None);

        var inRow = Assert.Single(result.Inbound);
        var outRow = Assert.Single(result.Outbound);
        Assert.Equal(10, inRow.PlannedPallets);
        Assert.Equal("Standard", inRow.PalletType);
        Assert.Equal(8, outRow.PlannedPallets);
        Assert.Equal("Euro", outRow.PalletType);
        Assert.Null(inRow.Difference);
        Assert.Equal(18, result.Totals.InboundPallets + result.Totals.OutboundPallets);
        Assert.DoesNotContain(result.Inbound.Concat(result.Outbound), x => x.LoadReference == "LOAD-BULK");
    }

    [Fact]
    public async Task Inbound_truck_shows_live_eta_and_warehouse_count_is_audited()
    {
        await using var db = CreateDb();
        var site = new Site { ExternalCode = "SLH-FRV", Name = "SLH-Lyons Consolidation Centre FRV" };
        var load = new Load { Reference = "RUN 9 PM", PlanningDate = new DateOnly(2026, 8, 24), Stops =
        [
            new() { LoadId = Guid.Empty, Sequence = 1, Name = "Deliver · SLH-Lyons Consolidation Centre FRV", PlannedArrivalUtc = new DateTimeOffset(2026, 8, 24, 17, 0, 0, TimeSpan.Zero) }
        ] };
        foreach (var stop in load.Stops) stop.LoadId = load.Id;
        db.AddRange(site, load);
        var inbound = AddLine(db, "Farm A", site.Name, 12, "Standard", "LOAD-ETA");
        AddAllocation(db, load.Id, inbound.OrderId, inbound.LineId, 12);
        var eta = new DateTimeOffset(2026, 8, 24, 17, 40, 0, TimeSpan.Zero);
        db.EtaSnapshots.Add(new EtaSnapshot { LoadId = load.Id, StopId = load.Stops[0].Id, EtaUtc = eta, CapturedAtUtc = eta.AddHours(-1), Source = "Samsara", Risk = "Late", TachoStatus = "OK" });
        await db.SaveChangesAsync();

        var service = new WarehouseMovementService(db);
        var row = Assert.Single((await service.BuildDailyAsync(new DateOnly(2026, 8, 24), CancellationToken.None)).Inbound);
        Assert.Equal(eta, row.LiveEtaUtc);
        Assert.Equal("Late", row.EtaRisk);
        Assert.NotNull(row.MovementKey);

        await service.RecordCheckAsync(new WarehouseCheckRequest(row.MovementKey!, new DateOnly(2026, 8, 24), "Inbound", 13, "1 extra"), "warehouse@lyonshaulage.com", CancellationToken.None);
        await service.RecordCheckAsync(new WarehouseCheckRequest(row.MovementKey!, new DateOnly(2026, 8, 24), "Inbound", 11, "recount"), "warehouse@lyonshaulage.com", CancellationToken.None);

        var checkedRow = Assert.Single((await service.BuildDailyAsync(new DateOnly(2026, 8, 24), CancellationToken.None)).Inbound);
        Assert.Equal(11, checkedRow.ActualQuantity);
        Assert.Equal(-1, checkedRow.Difference);
        Assert.Equal(2, checkedRow.CheckHistory!.Count);
        Assert.Equal("Received", checkedRow.CheckHistory![0].Action);
    }

    [Fact]
    public async Task Customer_drop_off_is_warehouse_inbound_and_its_reload_is_outbound_until_planned()
    {
        await using var db = CreateDb();
        db.Sites.Add(new Site { ExternalCode = "SLH-FRV", Name = "SLH-Lyons Consolidation Centre FRV", Aliases = "Stuart Lyons Distribution" });
        var date = new DateOnly(2026, 10, 17);
        var staged = new StagedImport
        {
            EntityType = "order", IdempotencyKey = Guid.NewGuid().ToString(), Status = StagingStatus.Promoted,
            PayloadJson = JsonSerializer.Serialize(new
            {
                customerCode = "WAITROSE", pallets = 3, sellerName = "Stuart Lyons Distribution", stallNumber = "Brinklow",
                warehouseDropOff = new { from = "Emery Soft Fruits", date = "2026-10-17", expected = "PM" }
            })
        };
        db.StagedImports.Add(staged);
        db.TransportOrders.Add(new TransportOrder { SourceStagedImportId = staged.Id, Reference = "EMERY-BRINKLOW", CustomerCode = "WAITROSE", CollectionDate = date, DeliveryDate = date.AddDays(1), Pallets = 3, SellerName = "Stuart Lyons Distribution", StallNumber = "Brinklow" });
        await db.SaveChangesAsync();

        var result = await new WarehouseMovementService(db).BuildDailyAsync(date, CancellationToken.None);

        var dropOff = Assert.Single(result.Inbound);
        Assert.Equal("Customer drop-off", dropOff.RunReference);
        Assert.Equal("Emery Soft Fruits", dropOff.From);
        Assert.Equal("PM", dropOff.Period);
        Assert.Equal(3, dropOff.PlannedPallets);
        var reload = Assert.Single(result.Outbound);
        Assert.Equal("Not on a run yet", reload.RunReference);
        Assert.Equal("Brinklow", reload.To);
    }

    private static (Guid OrderId, Guid LineId) AddLine(TmsDbContext db, string from, string to, int pallets, string type, string loadReference)
    {
        var staged = new StagedImport { EntityType = "order", IdempotencyKey = Guid.NewGuid().ToString(), Status = StagingStatus.Promoted, PayloadJson = "{}" };
        var movement = new OrderMovement { CustomerCode = "COOP", StableMovementKey = Guid.NewGuid().ToString(), LifecycleStatus = OrderMovementStatus.PlannerReady };
        var revision = new OrderRevision { MovementId = movement.Id, StagedImportId = staged.Id, RevisionNumber = 1, PayloadJson = "{}" };
        movement.CurrentRevisionId = revision.Id;
        var line = new OrderSourceLine { RevisionId = revision.Id, SourceRowKey = "1", CollectionSite = from, DeliverySite = to, CollectionDate = new DateOnly(2026, 8, 24), DeliveryDate = new DateOnly(2026, 8, 25), CollectionTimeFrom = new TimeOnly(18, 0), Pallets = pallets, PalletType = type, LoadReference = loadReference, PayloadJson = "{}" };
        var order = new TransportOrder { SourceStagedImportId = staged.Id, SourceMovementId = movement.Id, Reference = ($"PO-{Guid.NewGuid():N}")[..20], CustomerCode = "COOP", CollectionDate = new DateOnly(2026, 8, 24), DeliveryDate = new DateOnly(2026, 8, 25), Pallets = pallets };
        db.AddRange(staged, movement, revision, line, order);
        return (order.Id, line.Id);
    }

    private static void AddAllocation(TmsDbContext db, Guid loadId, Guid orderId, Guid lineId, int pallets) => db.StagedImports.Add(new StagedImport
    {
        EntityType = "planningpalletallocation", IdempotencyKey = Guid.NewGuid().ToString(), Status = StagingStatus.Promoted,
        PayloadJson = JsonSerializer.Serialize(new { orderId, loadId, pallets, date = "2026-08-24", updatedAtUtc = DateTimeOffset.UtcNow, sourceLineId = lineId })
    });

    private static TmsDbContext CreateDb() => new(new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
