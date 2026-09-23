using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RetainedOrderEvidenceReplayTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public RetainedOrderEvidenceReplayTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Replay_refreshes_unamended_pending_order_and_is_idempotent_on_second_run()
    {
        var messageId = $"replay-hhp-{Guid.NewGuid():N}";
        var evidenceId = Guid.NewGuid();
        var staleId = Guid.NewGuid();
        var key = $"email:{new string(messageId.Where(char.IsLetterOrDigit).ToArray())}:waitrose-direct-depot-1";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(new StagedImport
            {
                Id = evidenceId,
                EntityType = "email-evidence",
                IdempotencyKey = $"email-evidence:{new string(messageId.Where(char.IsLetterOrDigit).ToArray())}",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    messageId,
                    mailbox = "info@lyonshaulage.com",
                    senderAddress = "chris.benning@primafruit.co.uk",
                    senderName = "Chris Benning",
                    subject = "HHP WAITROSE DIRECT DEPOT DELIVERY 19.9.26",
                    receivedAtUtc = "2026-09-18T08:07:40Z",
                    bodyText = "Please collect 3 pallets from Hall Hunter today 18/09/2026.\n* Leyland 3 pallets\nFor Delivery date Saturday 19/09/2026.\nPO number: A65681. 95 cases of Strawberries.",
                    bodyFormat = "text",
                    attachments = Array.Empty<object>(),
                    evidenceAvailable = true
                }),
                Status = StagingStatus.Archived,
                Source = "Info mailbox evidence / chris.benning@primafruit.co.uk",
                ReceivedAtUtc = DateTimeOffset.Parse("2026-09-18T08:07:40Z")
            });
            db.StagedImports.Add(new StagedImport
            {
                Id = staleId,
                EntityType = "order",
                IdempotencyKey = key,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    poNumber = "A65681",
                    customerCode = "WRONG",
                    collectionDate = "2026-09-18",
                    deliveryDate = "2026-09-19",
                    pallets = 3,
                    sellerName = "Wrong site",
                    stallNumber = "Wrong destination",
                    sourceMessageId = messageId
                }),
                Status = StagingStatus.PendingReview,
                Source = "Info mailbox / old-parser@example.test",
                ReceivedAtUtc = DateTimeOffset.Parse("2026-09-18T08:07:41Z")
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Approve");
        var request = JsonSerializer.Serialize(new
        {
            receivedFromUtc = "2026-09-15T00:00:00Z",
            minimumPlanningDate = "2026-09-19",
            refreshUnamendedPending = true,
            maxMessages = 50
        });

        var first = await client.PostAsync(
            "/api/v1/order-intake/replay-retained-evidence",
            new StringContent(request, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var stale = await db.StagedImports.FindAsync(staleId);
            Assert.NotNull(stale);
            Assert.Equal(StagingStatus.Archived, stale!.Status);

            var active = db.StagedImports
                .Where(item => item.EntityType == "order" &&
                               item.Status == StagingStatus.PendingReview &&
                               item.PayloadJson.Contains(messageId))
                .ToList();
            var order = Assert.Single(active);
            using var payload = JsonDocument.Parse(order.PayloadJson);
            Assert.Equal("WAITROSE", payload.RootElement.GetProperty("customerCode").GetString());
            Assert.Equal("Hall Hunter", payload.RootElement.GetProperty("sellerName").GetString());
            Assert.Equal("Leyland", payload.RootElement.GetProperty("stallNumber").GetString());
            Assert.Equal(3, payload.RootElement.GetProperty("pallets").GetInt32());
            Assert.StartsWith("Info mailbox replay /", order.Source);
        }

        var second = await client.PostAsync(
            "/api/v1/order-intake/replay-retained-evidence",
            new StringContent(request, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var active = db.StagedImports
                .Where(item => item.EntityType == "order" &&
                               item.Status == StagingStatus.PendingReview &&
                               item.PayloadJson.Contains(messageId))
                .ToList();
            Assert.Single(active);
        }
    }

    [Fact]
    public async Task Replay_does_not_stage_unknown_evidence()
    {
        var messageId = $"replay-unknown-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "email-evidence",
                IdempotencyKey = $"email-evidence:{new string(messageId.Where(char.IsLetterOrDigit).ToArray())}",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    messageId,
                    mailbox = "info@lyonshaulage.com",
                    senderAddress = "sales@example.test",
                    subject = "Possible transport job 21/09",
                    receivedAtUtc = "2026-09-18T09:00:00Z",
                    bodyText = "Can you collect 12 pallets Monday and deliver them to Birmingham?",
                    attachments = Array.Empty<object>(),
                    evidenceAvailable = true
                }),
                Status = StagingStatus.Archived,
                Source = "Info mailbox evidence / sales@example.test",
                ReceivedAtUtc = DateTimeOffset.Parse("2026-09-18T09:00:00Z")
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Approve");
        var response = await client.PostAsync(
            "/api/v1/order-intake/replay-retained-evidence",
            new StringContent(JsonSerializer.Serialize(new
            {
                receivedFromUtc = "2026-09-15T00:00:00Z",
                minimumPlanningDate = "2026-09-19"
            }), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var checkScope = factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.DoesNotContain(checkDb.StagedImports,
            item => item.EntityType == "order" && item.PayloadJson.Contains(messageId));
    }
}
