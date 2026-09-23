using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class BulkOrderAmendmentApprovalTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public BulkOrderAmendmentApprovalTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Bulk_approval_consolidates_pm_overnight_amendment_and_removes_it_from_review_queue()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var reference = $"AMEND-{suffix}";
        var collectionDate = new DateOnly(2026, 9, 10);
        var planningDate = collectionDate.AddDays(1);

        var original = await AddPendingOrder(reference, collectionDate, planningDate, 4, "17:30", $"original-{suffix}");
        var originalApproval = await PostJson(client, $"/api/v1/staging/{original}/approve", new { note = "Original order approved" });
        Assert.Equal(HttpStatusCode.OK, originalApproval.StatusCode);

        Guid liveOrderId;
        Guid movementId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var live = Assert.Single(await db.TransportOrders.Where(x => x.SourceStagedImportId == original).ToListAsync());
            liveOrderId = live.Id;
            movementId = Assert.IsType<Guid>(live.SourceMovementId);
            Assert.Equal(4, live.Pallets);
        }

        var amendment = await AddPendingOrder(reference, collectionDate, planningDate, 7, "17:30", $"amendment-{suffix}");

        var bulkApproval = await PostJson(client, "/api/v1/staging/orders/bulk-approve", new
        {
            date = planningDate,
            ids = new[] { amendment },
            acknowledgeReviewFlags = true
        });

        Assert.Equal(HttpStatusCode.OK, bulkApproval.StatusCode);
        var body = await Json(bulkApproval);
        Assert.Equal(1, body.GetProperty("approved").GetInt32());
        Assert.Equal(0, body.GetProperty("skipped").GetInt32());
        Assert.Equal(0, body.GetProperty("failed").GetInt32());
        Assert.Contains(amendment, body.GetProperty("approvedIds").EnumerateArray().Select(x => x.GetGuid()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.Contains("removed from the review queue", body.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var stagedAmendment = await db.StagedImports.SingleAsync(x => x.Id == amendment);
            Assert.Equal(StagingStatus.Promoted, stagedAmendment.Status);
            Assert.DoesNotContain(await db.StagedImports.Where(x => x.Status == StagingStatus.PendingReview).ToListAsync(), x => x.Id == amendment);

            var liveOrders = await db.TransportOrders.Where(x => x.SourceMovementId == movementId).ToListAsync();
            var live = Assert.Single(liveOrders);
            Assert.Equal(liveOrderId, live.Id);
            Assert.Equal(7, live.Pallets);
            Assert.Equal(amendment, live.SourceStagedImportId);

            var movement = await db.OrderMovements.SingleAsync(x => x.Id == movementId);
            var revisions = await db.OrderRevisions.Where(x => x.MovementId == movementId).OrderBy(x => x.RevisionNumber).ToListAsync();
            Assert.Equal(2, revisions.Count);
            Assert.Equal(revisions[0].Id, revisions[1].SupersedesRevisionId);
            Assert.Equal(revisions[1].Id, movement.CurrentRevisionId);
        }
    }

    private async Task<Guid> AddPendingOrder(string reference, DateOnly collectionDate, DateOnly deliveryDate, int pallets, string requestedTime, string idempotencyKey)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var item = new StagedImport
        {
            EntityType = "order",
            IdempotencyKey = idempotencyKey,
            Status = StagingStatus.PendingReview,
            Source = "Amendment regression test",
            PayloadJson = JsonSerializer.Serialize(new
            {
                poNumber = reference,
                customerCode = "TEST",
                collectionDate,
                deliveryDate,
                pallets,
                sellerName = "Test Collection",
                stallNumber = "Test Delivery",
                requestedTime,
                plannerReady = true,
                intakeStatus = "PlannerReady",
                intakeConfidence = "High",
                intakeWarnings = Array.Empty<string>()
            })
        };
        db.StagedImports.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private static async Task<HttpResponseMessage> PostJson(HttpClient client, string url, object payload) =>
        await client.PostAsync(url, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
