using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

/// <summary>
/// The cached-email recovery screen (GET /api/v1/order-intake/cache) and its force-review
/// endpoints used to find an existing order by scanning every "order" StagedImport row and
/// substring-matching its payload against each cached email in memory. That pattern timed out
/// (SQL error 258, HTTP 504 on the GET) once order volume grew. These tests pin the bounded,
/// per-message-id lookup that replaced it.
/// </summary>
public sealed class OrderIntakeCacheReplayControllerTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public OrderIntakeCacheReplayControllerTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task List_reports_existing_order_count_for_cached_evidence_with_a_matching_order()
    {
        var date = new DateOnly(2026, 10, 21);
        var receivedAtUtc = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        var messageId = $"msg-{Guid.NewGuid():N}";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "email-evidence",
                IdempotencyKey = $"evidence-{Guid.NewGuid():N}",
                ReceivedAtUtc = receivedAtUtc,
                PayloadJson = JsonSerializer.Serialize(new { messageId, senderAddress = "orders@example.com", subject = "Order for 21 Oct", receivedAtUtc, attachments = Array.Empty<object>() })
            });
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "order",
                IdempotencyKey = $"order-{Guid.NewGuid():N}",
                ReceivedAtUtc = receivedAtUtc.AddMinutes(5),
                Status = StagingStatus.Promoted,
                PayloadJson = JsonSerializer.Serialize(new { poNumber = "PO-1", sourceMessageId = messageId })
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Read");
        using var response = JsonDocument.Parse(await (await client.GetAsync($"/api/v1/order-intake/cache?date={date:yyyy-MM-dd}")).Content.ReadAsStringAsync());
        var record = Assert.Single(response.RootElement.GetProperty("records").EnumerateArray().Where(x => x.GetProperty("messageId").GetString() == messageId));
        Assert.Equal(1, record.GetProperty("existingOrderCount").GetInt32());
        Assert.False(record.GetProperty("canForceReview").GetBoolean());
    }

    [Fact]
    public async Task ForceReview_does_not_duplicate_when_an_order_already_exists_within_the_search_window()
    {
        var date = new DateOnly(2026, 10, 21);
        var receivedAtUtc = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        var messageId = $"msg-{Guid.NewGuid():N}";
        Guid evidenceId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var evidence = new StagedImport
            {
                EntityType = "email-evidence",
                IdempotencyKey = $"evidence-{Guid.NewGuid():N}",
                ReceivedAtUtc = receivedAtUtc,
                PayloadJson = JsonSerializer.Serialize(new { messageId, senderAddress = "orders@example.com", subject = "Order for 21 Oct", receivedAtUtc, attachments = Array.Empty<object>() })
            };
            db.StagedImports.Add(evidence);
            // Order created a few days after the email - still inside the bounded search window.
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "order",
                IdempotencyKey = $"order-{Guid.NewGuid():N}",
                ReceivedAtUtc = receivedAtUtc.AddDays(3),
                Status = StagingStatus.Promoted,
                PayloadJson = JsonSerializer.Serialize(new { poNumber = "PO-1", sourceMessageId = messageId })
            });
            await db.SaveChangesAsync();
            evidenceId = evidence.Id;
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var result = await client.PostAsync($"/api/v1/order-intake/cache/{evidenceId}/force-review", new StringContent(string.Empty, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
        using var response = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal("existing_order_found", response.RootElement.GetProperty("status").GetString());

        using var scopeCheck = factory.Services.CreateScope();
        var checkDb = scopeCheck.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.DoesNotContain(checkDb.StagedImports, x => x.EntityType == "order" && x.IdempotencyKey.StartsWith("manual-email-review:"));
    }
}
