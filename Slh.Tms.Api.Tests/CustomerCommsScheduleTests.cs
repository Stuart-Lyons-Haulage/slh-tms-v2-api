using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class CustomerCommsScheduleTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public CustomerCommsScheduleTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Schedule_defaults_to_nobody_and_saves_per_customer_choices()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var initial = await client.GetFromJsonAsync<JsonElement>("/api/v1/customer-load-plans/schedule");
        Assert.Equal("16:30", initial.GetProperty("loadPlanTime").GetString());
        Assert.Equal("10:30", initial.GetProperty("deliveryTimesTime").GetString());

        var save = await client.PutAsJsonAsync("/api/v1/customer-load-plans/schedule", new
        {
            loadPlanTime = "16:45", deliveryTimesTime = "10:15", loadPlanLatest = "19:00",
            customers = new Dictionary<string, object> { ["NWF"] = new { loadPlan = true, deliveryTimes = true } }
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/customer-load-plans/schedule");
        Assert.Equal("16:45", after.GetProperty("loadPlanTime").GetString());
        var nwf = Assert.Single(after.GetProperty("customers").EnumerateArray(), item => item.GetProperty("customerCode").GetString() == "NWF");
        Assert.True(nwf.GetProperty("loadPlan").GetBoolean());

        var bad = await client.PutAsJsonAsync("/api/v1/customer-load-plans/schedule", new { loadPlanTime = "late", deliveryTimesTime = "10:15", loadPlanLatest = "19:00", customers = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Load_plan_is_held_while_an_order_is_not_on_a_run()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var date = new DateOnly(2031, 3, 4);
        db.TransportOrders.Add(new TransportOrder { Reference = $"HOLD-{Guid.NewGuid():N}"[..20], CustomerCode = "HOLDTEST", CollectionDate = date, Pallets = 10 });
        await db.SaveChangesAsync();

        var controller = new CustomerLoadPlansController(db, NullLogger<CustomerLoadPlansController>.Instance);
        var outcome = await controller.QueueAutomaticAsync(date, "HOLDTEST", deliveryTimes: false, CancellationToken.None);
        Assert.Equal("Held: 1 order not on a run yet", outcome);
    }
}
