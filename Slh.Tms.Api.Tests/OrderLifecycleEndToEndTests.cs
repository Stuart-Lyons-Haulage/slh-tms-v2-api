using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderLifecycleEndToEndTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public OrderLifecycleEndToEndTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Raw_email_is_staged_promoted_allocated_and_retains_quantity_end_to_end()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var messageId = $"e2e-order-{suffix}";
        var po = $"E2E{suffix}";
        var canonicalReference = $"{po}/LEYLAND";
        var planningDate = new DateOnly(2026, 9, 8);

        var intake = await PostJson(client, "/api/v1/order-intake/email", MailboxOrder(messageId, po, 4, planningDate, planningDate.AddDays(1)));
        Assert.Equal(HttpStatusCode.Accepted, intake.StatusCode);

        Guid stagedId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var staged = Assert.Single(await db.StagedImports.Where(x => x.EntityType == "order" && x.PayloadJson.Contains(messageId)).ToListAsync());
            stagedId = staged.Id;
            Assert.Equal(StagingStatus.PendingReview, staged.Status);
            using var payload = JsonDocument.Parse(staged.PayloadJson);
            Assert.Equal(4, payload.RootElement.GetProperty("pallets").GetInt32());
            Assert.Equal(canonicalReference, payload.RootElement.GetProperty("poNumber").GetString());
        }

        var approve = await PostJson(client, $"/api/v1/staging/{stagedId}/approve", new { note = "E2E lifecycle approval" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        TransportOrder order;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            order = Assert.Single(await db.TransportOrders.Where(x => x.SourceStagedImportId == stagedId).ToListAsync());
            Assert.Equal(canonicalReference, order.Reference);
            Assert.Equal(4, order.Pallets);
            Assert.Equal(planningDate, order.CollectionDate);
        }

        var createRun = await PostJson(client, "/api/v1/loads", new
        {
            reference = $"E2E-RUN-{suffix}", planningDate,
            vehicleId = (Guid?)null, driverId = (Guid?)null, trailerId = (Guid?)null,
            stops = new[] { new { orderId = (Guid?)null, name = "Collect · E2E origin", address = "Test origin", latitude = (decimal?)null, longitude = (decimal?)null, plannedArrivalUtc = new DateTimeOffset(2026, 9, 8, 8, 0, 0, TimeSpan.Zero) } },
            palletSpacesUsed = (decimal?)0, totalPalletSpaces = (decimal?)26, capacityType = "Standard pallets"
        });
        Assert.Equal(HttpStatusCode.Created, createRun.StatusCode);
        var runId = (await Json(createRun)).GetProperty("id").GetGuid();

        var allocate = await PostJson(client, "/api/v1/planning-control/allocations", new { orderId = order.Id, loadId = runId, date = planningDate, pallets = 4, note = "E2E quantity allocation" });
        Assert.Equal(HttpStatusCode.OK, allocate.StatusCode);
        var allocation = await Json(allocate);
        Assert.Equal(4, allocation.GetProperty("allocatedToRun").GetInt32());
        Assert.Equal(4, allocation.GetProperty("plannedPallets").GetInt32());
        Assert.Equal(4, allocation.GetProperty("orderedPallets").GetInt32());
        Assert.Equal(0, allocation.GetProperty("outstandingPallets").GetInt32());

        var control = await client.GetAsync($"/api/v1/planning-control/pallets?date={planningDate:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        var controlJson = await Json(control);
        var orderRow = controlJson.GetProperty("orders").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == order.Id);
        Assert.Equal(4, orderRow.GetProperty("orderedPallets").GetInt32());
        Assert.Equal(4, orderRow.GetProperty("plannedPallets").GetInt32());
        Assert.Equal(0, orderRow.GetProperty("outstandingPallets").GetInt32());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var run = await db.Loads.Include(x => x.Stops).SingleAsync(x => x.Id == runId);
            Assert.Contains(run.Stops, x => x.OrderId == order.Id);
            Assert.Equal(4, (await db.TransportOrders.SingleAsync(x => x.Id == order.Id)).Pallets);
        }
    }

    [Fact]
    public async Task Revised_email_creates_new_revision_not_duplicate_and_exposes_affected_run_allocation()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var po = $"REV{suffix}";
        var planningDate = new DateOnly(2026, 9, 9);

        var originalMessage = $"e2e-revision-original-{suffix}";
        Assert.Equal(HttpStatusCode.Accepted, (await PostJson(client, "/api/v1/order-intake/email", MailboxOrder(originalMessage, po, 4, planningDate, planningDate.AddDays(1)))).StatusCode);
        var originalStagedId = await FindStagingId(originalMessage);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/staging/{originalStagedId}/approve", new { note = "Original approved" })).StatusCode);

        TransportOrder order;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            order = Assert.Single(await db.TransportOrders.Where(x => x.SourceStagedImportId == originalStagedId).ToListAsync());
        }

        var createRun = await PostJson(client, "/api/v1/loads", new
        {
            reference = $"REV-RUN-{suffix}", planningDate,
            stops = new[] { new { orderId = (Guid?)null, name = "Collect · revision test", address = (string?)null, latitude = (decimal?)null, longitude = (decimal?)null, plannedArrivalUtc = (DateTimeOffset?)null } },
            palletSpacesUsed = (decimal?)0, totalPalletSpaces = (decimal?)26, capacityType = "Standard pallets"
        });
        Assert.Equal(HttpStatusCode.Created, createRun.StatusCode);
        var loadId = (await Json(createRun)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/v1/planning-control/allocations", new { orderId = order.Id, loadId, date = planningDate, pallets = 4, note = "Original allocation" })).StatusCode);

        var revisedMessage = $"e2e-revision-amended-{suffix}";
        var revisedResponse = await PostJson(client, "/api/v1/order-intake/email", MailboxOrder(revisedMessage, po, 7, planningDate, planningDate.AddDays(1)));
        Assert.Equal(HttpStatusCode.Accepted, revisedResponse.StatusCode);
        var revisedBody = await Json(revisedResponse);
        Assert.Equal(1, revisedBody.GetProperty("staged").GetInt32());
        Assert.Equal(0, revisedBody.GetProperty("existing").GetInt32());

        var revisedStagedId = await FindStagingId(revisedMessage);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/staging/{revisedStagedId}/approve", new { note = "Revision approved" })).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var movement = Assert.Single(await db.OrderMovements.Where(x => x.Id == order.SourceMovementId).ToListAsync());
            var revisions = await db.OrderRevisions.Where(x => x.MovementId == movement.Id).OrderBy(x => x.RevisionNumber).ToListAsync();
            Assert.Equal(2, revisions.Count);
            Assert.Equal(revisions[0].Id, revisions[1].SupersedesRevisionId);
            Assert.Equal(revisions[1].Id, movement.CurrentRevisionId);
            var updatedOrder = await db.TransportOrders.SingleAsync(x => x.Id == order.Id);
            Assert.Equal(7, updatedOrder.Pallets);
            Assert.Equal(revisedStagedId, updatedOrder.SourceStagedImportId);
        }

        var planning = await client.GetAsync($"/api/v1/planning-control/pallets?date={planningDate:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, planning.StatusCode);
        var planningJson = await Json(planning);
        var orderRow = planningJson.GetProperty("orders").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == order.Id);
        Assert.Equal(7, orderRow.GetProperty("orderedPallets").GetInt32());
        Assert.Equal(4, orderRow.GetProperty("plannedPallets").GetInt32());
        Assert.Equal(3, orderRow.GetProperty("outstandingPallets").GetInt32());
        Assert.Contains(orderRow.GetProperty("allocations").EnumerateArray(), allocation => allocation.GetProperty("loadId").GetGuid() == loadId && allocation.GetProperty("pallets").GetInt32() == 4);
    }

    private async Task<Guid> FindStagingId(string messageId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        return (await db.StagedImports.SingleAsync(x => x.EntityType == "order" && x.PayloadJson.Contains(messageId))).Id;
    }

    private static object MailboxOrder(string messageId, string po, int pallets, DateOnly collectionDate, DateOnly deliveryDate) => new
    {
        messageId, internetMessageId = $"<{messageId}@example.test>", mailbox = "info@lyonshaulage.com",
        senderAddress = "orders@summerberry.co.uk", senderName = "Summer Berry",
        subject = $"HHP WAITROSE DIRECT DEPOT DELIVERY {deliveryDate:dd/MM/yy}",
        receivedAtUtc = new DateTimeOffset(collectionDate.Year, collectionDate.Month, collectionDate.Day, 8, 0, 0, TimeSpan.Zero),
        bodyText = $"Please collect {pallets} pallets from Summer Berry today {collectionDate:dd/MM/yyyy}.\n* Leyland {pallets} pallets\nFor Delivery date {deliveryDate:dd/MM/yyyy}.\nPO number: {po}. 174 cases of Berries.",
        webLink = "https://outlook.office.com/mail/e2e"
    };

    private static async Task<HttpResponseMessage> PostJson(HttpClient client, string url, object payload) =>
        await client.PostAsync(url, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
