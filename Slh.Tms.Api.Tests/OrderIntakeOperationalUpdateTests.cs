using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeOperationalUpdateTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public OrderIntakeOperationalUpdateTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task ReadyToCollectEmail_IsEvidenceLinkedWithoutCreatingAnotherOrder()
    {
        var suffix = Guid.NewGuid().ToString("N");
        Guid orderId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var order = new StagedImport
            {
                EntityType = "order",
                IdempotencyKey = $"waitrose-status-target-{suffix}",
                Status = StagingStatus.PendingReview,
                Source = "Info mailbox",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    customerCode = "WAITROSE", sellerName = "Leythorne", collectionDate = "2026-09-08",
                    deliveryDate = "2026-09-09", customerPo = $"PO-{suffix}", pallets = 2
                })
            };
            db.StagedImports.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var messageId = $"waitrose-ready-{suffix}";
        var request = JsonSerializer.Serialize(new
        {
            messageId, mailbox = "info@lyonshaulage.com", senderAddress = "Goods.INNV@barfoots.co.uk",
            subject = "Waitrose", receivedAtUtc = "2026-09-08T11:59:40Z",
            bodyText = "Waitrose is ready to be collected from Leythorne."
        });

        var first = await client.PostAsync("/api/v1/order-intake/email", new StringContent(request, Encoding.UTF8, "application/json"));
        var second = await client.PostAsync("/api/v1/order-intake/email", new StringContent(request, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("\"linked\":1", await first.Content.ReadAsStringAsync());
        Assert.Contains("\"linked\":0", await second.Content.ReadAsStringAsync());

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Single(await verifyDb.StagedImportEvents.Where(item => item.StagedImportId == orderId && item.EventType == "OperationalUpdateLinked").ToListAsync());
        Assert.Single(await verifyDb.StagedImports.Where(item => item.EntityType == "email-evidence" && item.PayloadJson.Contains(messageId)).ToListAsync());
        Assert.Single(await verifyDb.StagedImports.Where(item => item.EntityType == "order" && item.Id == orderId).ToListAsync());
    }

    [Fact]
    public async Task PluralReadyForCollectionStatus_IsIgnoredAsOperationalUpdate()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var request = JsonSerializer.Serialize(new
        {
            messageId = $"market-ready-{Guid.NewGuid():N}", mailbox = "info@lyonshaulage.com",
            senderAddress = "ilia.angelakidis@barfoots.co.uk", subject = "Waitrose + Market DD 10/09/26",
            receivedAtUtc = "2026-09-09T11:12:00Z",
            bodyText = "Waitrose and Markets pallets are ready for collection from Leythorne."
        });
        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(request, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"ignored\":true", text);
        Assert.Contains("\"staged\":0", text);
    }
}
