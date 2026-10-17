using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

/// <summary>
/// Delivery times replace the hand-built "Daily Times" / "Aldi Times" emails: planned
/// delivery time plus the latest live ETA from tracking, queued on the existing
/// Info-mailbox outbox so the current Power Automate flow sends it.
/// </summary>
public sealed class CustomerDeliveryTimesTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public CustomerDeliveryTimesTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Delivery_times_show_live_eta_and_queue_on_the_existing_outbox()
    {
        var date = new DateOnly(2026, 10, 21);
        const string customer = "TESTDT";
        Guid orderId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Customers.Add(new Customer { Code = customer, Name = "Test Delivery Customer" });
            db.CustomerContacts.Add(new CustomerContact { CustomerCode = customer, Name = "Depot desk", Email = "depot@example.test" });
            var order = new TransportOrder
            {
                Reference = "DT-ORDER-1",
                CustomerCode = customer,
                CollectionDate = date,
                DeliveryDate = date,
                Pallets = 6,
                SellerName = "Greenhouse",
                StallNumber = "ALDI-NESTON",
                DeliveryWindowEndUtc = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero) // 11:00 BST
            };
            db.TransportOrders.Add(order);
            orderId = order.Id;
            var load = new Load { Reference = "DT-RUN-1", PlanningDate = date, Status = LoadStatus.Planned };
            var collect = new LoadStop { LoadId = load.Id, OrderId = order.Id, Sequence = 1, Name = "Collect · Greenhouse", PlannedArrivalUtc = new DateTimeOffset(2026, 10, 21, 5, 0, 0, TimeSpan.Zero) };
            var deliver = new LoadStop { LoadId = load.Id, OrderId = order.Id, Sequence = 2, Name = "Deliver · ALDI-NESTON" };
            load.Stops.Add(collect);
            load.Stops.Add(deliver);
            db.Loads.Add(load);
            db.EtaSnapshots.Add(new EtaSnapshot
            {
                LoadId = load.Id,
                StopId = deliver.Id,
                OrderId = order.Id,
                EtaUtc = new DateTimeOffset(2026, 10, 21, 9, 15, 0, TimeSpan.Zero), // 10:15 BST
                CapturedAtUtc = new DateTimeOffset(2026, 10, 21, 7, 30, 0, TimeSpan.Zero),
                Source = "Samsara",
                Risk = "OnTime",
                TachoStatus = "OK"
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");

        var preview = await client.GetAsync($"/api/v1/customer-load-plans/delivery-times?date={date:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using (var document = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()))
        {
            var plan = document.RootElement.GetProperty("customers").EnumerateArray().Single(item => item.GetProperty("customerCode").GetString() == customer);
            Assert.Equal("Test Delivery Customer Delivery Times-21102026", plan.GetProperty("subject").GetString());
            var row = Assert.Single(plan.GetProperty("rows").EnumerateArray());
            Assert.Equal("ALDI-NESTON", row.GetProperty("deliverySite").GetString());
            Assert.Equal("11:00", row.GetProperty("plannedDelivery").GetString());
            Assert.Equal("10:15", row.GetProperty("liveEta").GetString());
            Assert.Equal("08:30", row.GetProperty("etaAsAt").GetString());
        }

        var queue = await client.PostAsync("/api/v1/customer-load-plans/delivery-times/queue",
            new StringContent(JsonSerializer.Serialize(new { planningDate = date, customerCode = customer }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, queue.StatusCode);

        var outbox = await client.GetAsync("/api/v1/customer-load-plans/outbox?take=50");
        Assert.Equal(HttpStatusCode.OK, outbox.StatusCode);
        using (var document = JsonDocument.Parse(await outbox.Content.ReadAsStringAsync()))
        {
            var item = document.RootElement.GetProperty("items").EnumerateArray().Single(entry => entry.GetProperty("customerCode").GetString() == customer);
            Assert.Equal("Test Delivery Customer Delivery Times-21102026", item.GetProperty("subject").GetString());
            var body = item.GetProperty("body").GetString()!;
            Assert.Contains("ALDI-NESTON", body);
            Assert.Contains(">10:15<", body);
            Assert.DoesNotContain("\n", body);
            var attachment = Assert.Single(item.GetProperty("attachments").EnumerateArray());
            Assert.Equal("Test Delivery Customer Delivery Times-21102026.pdf", attachment.GetProperty("name").GetString());
            Assert.Equal(new[] { "depot@example.test" }, item.GetProperty("to").EnumerateArray().Select(value => value.GetString()));
        }

        // The ordinary load plan is unaffected: sending delivery times is not a load-plan send.
        var loadPlans = await client.GetAsync($"/api/v1/customer-load-plans/preview?date={date:yyyy-MM-dd}");
        using (var document = JsonDocument.Parse(await loadPlans.Content.ReadAsStringAsync()))
        {
            var plan = document.RootElement.GetProperty("plans").EnumerateArray().Single(item => item.GetProperty("customerCode").GetString() == customer);
            Assert.Equal(JsonValueKind.Null, plan.GetProperty("lastSendStatus").ValueKind);
            Assert.Equal(orderId, plan.GetProperty("rows").EnumerateArray().Single().GetProperty("orderId").GetGuid());
        }
    }
}
