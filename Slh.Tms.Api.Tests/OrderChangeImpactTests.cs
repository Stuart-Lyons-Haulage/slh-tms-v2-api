using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderChangeImpactTests
{
    private static TmsDbContext Db() => new(new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public void Diff_lists_only_operational_changes()
    {
        var order = new TransportOrder { Reference = "R1", CustomerCode = "ALDI", CollectionDate = new DateOnly(2026, 10, 18), Pallets = 2, StallNumber = "Swindon" };
        var before = OrderChangeImpact.Snapshot(order);
        order.Pallets = 3;
        var changes = OrderChangeImpact.Diff(before, OrderChangeImpact.Snapshot(order));
        var change = Assert.Single(changes);
        Assert.Equal(("Quantity", "2", "3"), (change.Field, change.From, change.To));
    }

    [Fact]
    public async Task Change_on_a_run_sent_to_samsara_is_a_live_route_alert_until_actioned()
    {
        await using var db = Db();
        var order = new TransportOrder { Reference = "R1", CustomerCode = "ALDI", CollectionDate = new DateOnly(2026, 10, 18), Pallets = 3 };
        db.TransportOrders.Add(order);
        await db.SaveChangesAsync();

        var runs = new List<OrderChangeImpact.RunImpact> { new(Guid.NewGuid(), "RUN 4 PM", "Dispatched", "route-123", true) };
        var record = await OrderChangeImpact.RecordAsync(db, order, "Amended", [new("Quantity", "2", "3")], "Phil Sampson · CHANGE TO SUNDAY ALDI", "planner", CancellationToken.None, runs);
        await db.SaveChangesAsync();

        Assert.NotNull(record);
        Assert.Equal(StagingStatus.PendingReview, record!.Status);
        Assert.Contains("\"stage\":\"LiveRoute\"", record.PayloadJson);
        Assert.Contains("amend the driver", record.PayloadJson);
    }

    [Fact]
    public async Task Change_to_an_unplanned_order_is_recorded_but_needs_no_action()
    {
        await using var db = Db();
        var order = new TransportOrder { Reference = "R2", CustomerCode = "ALDI", CollectionDate = new DateOnly(2026, 10, 18), Pallets = 3 };
        var record = await OrderChangeImpact.RecordAsync(db, order, "Amended", [new("Quantity", "2", "3")], null, null, CancellationToken.None, []);
        Assert.Equal(StagingStatus.Approved, record!.Status);
        Assert.Contains("\"stage\":\"NotPlanned\"", record.PayloadJson);
    }
}
