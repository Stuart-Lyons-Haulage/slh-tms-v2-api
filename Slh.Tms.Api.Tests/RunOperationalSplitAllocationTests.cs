using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RunOperationalSplitAllocationTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public RunOperationalSplitAllocationTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Saving_single_order_run_quantity_updates_shared_pallet_allocation_balance()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var date = new DateOnly(2026, 8, 24);
        var order = new TransportOrder
        {
            Reference = $"AMZ-{Guid.NewGuid():N}"[..20],
            CustomerCode = "AMAZON",
            CollectionDate = date,
            Pallets = 3,
            Status = OrderStatus.ReadyToPlan
        };
        var load = new Load
        {
            Reference = $"RUN-{Guid.NewGuid():N}"[..20],
            PlanningDate = date,
            CapacityType = "Standard pallets",
            PalletSpacesUsed = 3,
            Stops =
            [
                new LoadStop { Sequence = 1, Name = "Collect · Amazon" },
                new LoadStop { Sequence = 2, Name = "Deliver · Amazon", OrderId = order.Id }
            ]
        };
        db.AddRange(order, load);
        await db.SaveChangesAsync();

        await RunOperationalStore.SaveAsync(
            db,
            load,
            new RunOperationalValues(2, 26, "Standard pallets", null, null, null),
            "planner@lyonshaulage.com",
            CancellationToken.None);

        var allocationRows = db.StagedImports
            .Where(x => x.EntityType == PlanningAllocationStore.EntityType && x.Status == StagingStatus.Promoted)
            .OrderByDescending(x => x.ReceivedAtUtc)
            .ToList();

        var matched = allocationRows.Select(row => JsonDocument.Parse(row.PayloadJson).RootElement)
            .FirstOrDefault(root => root.TryGetProperty("orderId", out var orderId) && orderId.GetGuid() == order.Id &&
                                    root.TryGetProperty("loadId", out var loadId) && loadId.GetGuid() == load.Id);

        Assert.Equal(JsonValueKind.Object, matched.ValueKind);
        Assert.Equal(2, matched.GetProperty("pallets").GetInt32());
    }
}