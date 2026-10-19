using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class BulkApprovalJobTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public BulkApprovalJobTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Large_selection_is_approved_in_the_background_and_reports_progress()
    {
        var ids = new List<Guid>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            for (var i = 0; i < 3; i++)
            {
                var item = new StagedImport
                {
                    EntityType = "order",
                    IdempotencyKey = $"bulk-job-{Guid.NewGuid():N}",
                    Status = StagingStatus.PendingReview,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        poNumber = $"BULKJOB-{Guid.NewGuid():N}"[..20], customerCode = "WAITROSE", collectionDate = "2026-10-19", deliveryDate = "2026-10-19",
                        pallets = 2 + i, sellerName = "Fowler Welch Hilsea", stallNumber = "Leyland", plannerReady = true, intakeConfidence = "High"
                    })
                };
                db.StagedImports.Add(item);
                ids.Add(item.Id);
            }
            // One that cannot be approved: wrong planning date.
            var wrongDate = new StagedImport
            {
                EntityType = "order", IdempotencyKey = $"bulk-job-{Guid.NewGuid():N}", Status = StagingStatus.PendingReview,
                PayloadJson = JsonSerializer.Serialize(new { poNumber = "BULKJOB-WRONGDATE", customerCode = "WAITROSE", collectionDate = "2026-10-25", pallets = 1, sellerName = "A", stallNumber = "B" })
            };
            db.StagedImports.Add(wrongDate);
            ids.Add(wrongDate.Id);
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var start = await client.PostAsJsonAsync("/api/v1/staging/orders/bulk-approve-jobs", new { date = "2026-10-19", ids, acknowledgeReviewFlags = true });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var jobId = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        JsonElement status = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            status = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/staging/orders/bulk-approve-jobs/{jobId}")).RootElement.Clone();
            if (status.GetProperty("done").GetBoolean()) break;
            await Task.Delay(100);
        }

        Assert.True(status.GetProperty("done").GetBoolean());
        Assert.Equal(4, status.GetProperty("processed").GetInt32());
        Assert.Equal(3, status.GetProperty("approved").GetInt32());
        Assert.Equal(1, status.GetProperty("skipped").GetInt32());
    }
}
