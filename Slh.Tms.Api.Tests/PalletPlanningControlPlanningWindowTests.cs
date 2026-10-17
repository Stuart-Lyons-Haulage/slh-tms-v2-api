using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PalletPlanningControlPlanningWindowTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public PalletPlanningControlPlanningWindowTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Planning_control_uses_collection_rows_and_delivery_columns_while_retaining_am_pm_metadata()
    {
        var collectionDate = new DateOnly(2026, 9, 15);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var am = new TransportOrder { Reference = $"AM-{Guid.NewGuid():N}", CustomerCode = "BAREFOOTS", CollectionDate = collectionDate, DeliveryDate = collectionDate, Pallets = 4, SellerName = "Barefoots AM", StallNumber = "Leyland" };
        var pm = new TransportOrder { Reference = $"PM-{Guid.NewGuid():N}", CustomerCode = "BAREFOOTS", CollectionDate = collectionDate, DeliveryDate = collectionDate.AddDays(1), Pallets = 6, SellerName = "Barefoots PM", StallNumber = "Leyland" };
        db.TransportOrders.AddRange(am, pm);
        await db.SaveChangesAsync();

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Read");
        var response = await client.GetAsync("/api/v1/planning-control/pallets?date=2026-09-15");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cells = document.RootElement.GetProperty("cells").EnumerateArray().ToList();
        Assert.Contains(cells, cell => cell.GetProperty("planningGroup").GetString() == "Barefoots AM" && cell.GetProperty("destination").GetString() == "Leyland");
        Assert.Contains(cells, cell => cell.GetProperty("planningGroup").GetString() == "Barefoots PM" && cell.GetProperty("destination").GetString() == "Leyland");
        Assert.Equal(new[] { "Leyland" }, document.RootElement.GetProperty("destinations").EnumerateArray().Select(value => value.GetString()).ToArray());

        var pmOrder = Assert.Single(document.RootElement.GetProperty("orders").EnumerateArray(), order => order.GetProperty("reference").GetString() == pm.Reference);
        Assert.Equal("Barefoots PM", pmOrder.GetProperty("planningGroup").GetString());
        Assert.Equal("Leyland", pmOrder.GetProperty("destination").GetString());
        Assert.Equal("PM Work", pmOrder.GetProperty("planningSection").GetString());
        Assert.Equal("PM Overnight", pmOrder.GetProperty("suggestedRouteType").GetString());
    }
}
