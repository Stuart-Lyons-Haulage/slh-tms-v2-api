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
            maximumPlanningDate = "2026-09-19",
            refreshUnamendedPending = true,
            maxMessages = 50
        });

        var first = await client.PostAsync(
            "/api/v1/order-intake/replay-retained-evidence",
            new StringContent(request, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstPayload = await first.Content.ReadAsStringAsync();
        using (var firstJson = JsonDocument.Parse(firstPayload))
        {
            Assert.True(
                firstJson.RootElement.TryGetProperty("pendingArchivedForRefresh", out var archivedCount) &&
                archivedCount.GetInt32() == 1,
                $"Replay did not archive exactly one stale row. Response: {firstPayload}");
        }

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
            Assert.DoesNotContain(active, item => item.IdempotencyKey.Contains(":replay:", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, active.Count(item => item.Source != null && item.Source.StartsWith("Info mailbox replay", StringComparison.Ordinal)));
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

    [Fact]
    public async Task Replay_returns_cursor_for_large_evidence_set()
    {
        var prefix = $"replay-cursor-{Guid.NewGuid():N}";
        var receivedFrom = DateTimeOffset.Parse("2026-09-27T00:00:00Z");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            for (var index = 0; index < 6; index++)
            {
                var messageId = $"{prefix}-{index}";
                db.StagedImports.Add(new StagedImport
                {
                    Id = Guid.NewGuid(),
                    EntityType = "email-evidence",
                    IdempotencyKey = $"email-evidence:{messageId}",
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        messageId,
                        mailbox = "info@lyonshaulage.com",
                        senderAddress = "unknown@example.test",
                        subject = "Unrecognised retained evidence",
                        receivedAtUtc = receivedFrom.AddMinutes(index).ToString("O"),
                        bodyText = "Please review this retained message.",
                        attachments = Array.Empty<object>(),
                        evidenceAvailable = true
                    }),
                    Status = StagingStatus.Archived,
                    Source = "Info mailbox evidence / unknown@example.test",
                    ReceivedAtUtc = receivedFrom.AddMinutes(index)
                });
            }
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Approve");
        var request = new
        {
            receivedFromUtc = receivedFrom,
            minimumPlanningDate = "2026-09-27",
            maxMessages = 5
        };

        var first = await client.PostAsync(
            "/api/v1/order-intake/replay-retained-evidence",
            new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.True(firstJson.RootElement.GetProperty("hasMore").GetBoolean());
        var nextReceived = firstJson.RootElement.GetProperty("nextAfterReceivedAtUtc").GetDateTimeOffset();
        var nextId = firstJson.RootElement.GetProperty("nextAfterEvidenceId").GetGuid();

        var second = await client.PostAsync(
            "/api/v1/order-intake/replay-retained-evidence",
            new StringContent(JsonSerializer.Serialize(new
            {
                receivedFromUtc = receivedFrom,
                minimumPlanningDate = "2026-09-27",
                maxMessages = 5,
                afterReceivedAtUtc = nextReceived,
                afterEvidenceId = nextId
            }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.False(secondJson.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.InRange(secondJson.RootElement.GetProperty("evidenceScanned").GetInt32(), 1, 20);
    }

    [Fact]
    public async Task Replay_malformed_waitrose_pdf_returns_controlled_result_and_retains_evidence()
    {
        var messageId = $"replay-waitrose-malformed-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "email-evidence",
                IdempotencyKey = $"email-evidence:{messageId}",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    messageId,
                    mailbox = "info@lyonshaulage.com",
                    senderAddress = "goods.innv@barfoots.co.uk",
                    subject = "WAITROSE booking",
                    receivedAtUtc = "2026-09-29T08:00:00Z",
                    bodyText = "Please review the attached Waitrose booking.",
                    attachments = new[] { new { name = "booking.pdf", contentType = "application/pdf", contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("not a PDF")) } },
                    evidenceAvailable = true
                }),
                Status = StagingStatus.Archived,
                Source = "Info mailbox evidence / goods.innv@barfoots.co.uk",
                ReceivedAtUtc = DateTimeOffset.Parse("2026-09-29T08:00:00Z")
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Approve");
        var response = await client.PostAsync(
            "/api/v1/order-intake/replay-retained-evidence",
            new StringContent(JsonSerializer.Serialize(new
            {
                receivedFromUtc = "2026-09-29T00:00:00Z",
                minimumPlanningDate = "2026-09-29"
            }), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalidEvidence", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staging_refresh_returns_waitrose_projection_without_reparsing_or_500()
    {
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(new StagedImport
            {
                Id = id,
                EntityType = "order",
                IdempotencyKey = $"refresh-waitrose-{id:N}",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    customerCode = "WAITROSE",
                    poNumber = "O79001/3564471",
                    pallets = 2,
                    casesOrdered = 196,
                    palletType = "Standard",
                    collectionDate = "2026-09-30",
                    deliveryDate = "2026-09-30"
                }),
                Status = StagingStatus.PendingReview,
                Source = "Info mailbox / goods.innv@barfoots.co.uk",
                ReceivedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Read");
        var response = await client.GetAsync("/api/v1/staging?entityType=order&take=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains(id.ToString(), payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("O79001/3564471", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_refreshes_target_projection_without_archiving_other_dates_from_same_email()
    {
        var messageId = $"replay-multi-date-{Guid.NewGuid():N}";
        var targetId = Guid.NewGuid();
        var otherDateId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "email-evidence",
                IdempotencyKey = $"email-evidence:{messageId}",
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
                Id = targetId,
                EntityType = "order",
                IdempotencyKey = $"old-target:{Guid.NewGuid():N}",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    poNumber = "A65681",
                    customerCode = "WAITROSE",
                    collectionDate = "2026-09-18",
                    deliveryDate = "2026-09-19",
                    pallets = 3,
                    sellerName = "Hall Hunter",
                    stallNumber = "Leyland",
                    sourceMessageId = messageId
                }),
                Status = StagingStatus.PendingReview,
                Source = "Info mailbox / old-parser@example.test",
                ReceivedAtUtc = DateTimeOffset.Parse("2026-09-18T08:07:41Z")
            });
            db.StagedImports.Add(new StagedImport
            {
                Id = otherDateId,
                EntityType = "order",
                IdempotencyKey = $"old-other-date:{Guid.NewGuid():N}",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    poNumber = "A65682",
                    customerCode = "WAITROSE",
                    collectionDate = "2026-09-18",
                    deliveryDate = "2026-09-18",
                    pallets = 2,
                    sellerName = "Hall Hunter",
                    stallNumber = "Aylesford",
                    sourceMessageId = messageId
                }),
                Status = StagingStatus.PendingReview,
                Source = "Info mailbox / old-parser@example.test",
                ReceivedAtUtc = DateTimeOffset.Parse("2026-09-18T08:07:42Z")
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Approve");
        var response = await client.PostAsync(
            "/api/v1/order-intake/replay-retained-evidence",
            new StringContent(JsonSerializer.Serialize(new
            {
                receivedFromUtc = "2026-09-15T00:00:00Z",
                minimumPlanningDate = "2026-09-19",
                maximumPlanningDate = "2026-09-19",
                refreshUnamendedPending = true,
                maxMessages = 5
            }), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responsePayload = await response.Content.ReadAsStringAsync();
        using (var result = JsonDocument.Parse(responsePayload))
        {
            Assert.Equal(1, result.RootElement.GetProperty("pendingArchivedForRefresh").GetInt32());
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            Assert.Equal(StagingStatus.Archived, (await db.StagedImports.FindAsync(targetId))!.Status);
            Assert.Equal(StagingStatus.PendingReview, (await db.StagedImports.FindAsync(otherDateId))!.Status);

            var activeForMessage = db.StagedImports
                .Where(item => item.EntityType == "order" &&
                               item.Status == StagingStatus.PendingReview &&
                               item.PayloadJson.Contains(messageId))
                .ToList();
            Assert.Equal(2, activeForMessage.Count);
            Assert.Contains(activeForMessage, item => item.Id == otherDateId);
            Assert.Contains(activeForMessage, item => item.Id != otherDateId &&
                                                       item.PayloadJson.Contains("A65681", StringComparison.Ordinal));
        }
    }
}
