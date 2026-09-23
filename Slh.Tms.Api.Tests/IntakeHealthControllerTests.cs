using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class IntakeHealthControllerTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public IntakeHealthControllerTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Intake_health_surfaces_pending_mapping_fast_path_and_failures()
    {
        var from = DateTimeOffset.UtcNow.AddSeconds(-2);
        var suffix = Guid.NewGuid().ToString("N")[..10];

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.AddRange(
                new StagedImport
                {
                    EntityType = "email-evidence",
                    IdempotencyKey = $"health-evidence-{suffix}",
                    PayloadJson = "{}",
                    Status = StagingStatus.Archived,
                    Source = "Info mailbox evidence / regression@example.com",
                    ReceivedAtUtc = DateTimeOffset.UtcNow
                },
                new StagedImport
                {
                    EntityType = "order",
                    IdempotencyKey = $"health-mapping-{suffix}",
                    PayloadJson = "{\"intakeStatus\":\"MappingException\"}",
                    Status = StagingStatus.PendingReview,
                    Source = "Info mailbox mapping exception / regression@example.com",
                    ReceivedAtUtc = DateTimeOffset.UtcNow
                },
                new StagedImport
                {
                    EntityType = "order",
                    IdempotencyKey = $"health-fast-{suffix}",
                    PayloadJson = "{\"emailIntakePath\":\"sender-route-fast-path\"}",
                    Status = StagingStatus.Promoted,
                    Source = "Info mailbox / known@example.com",
                    ReceivedAtUtc = DateTimeOffset.UtcNow,
                    ReviewedAtUtc = DateTimeOffset.UtcNow
                },
                new StagedImport
                {
                    EntityType = "order",
                    IdempotencyKey = $"health-failed-{suffix}",
                    PayloadJson = "{}",
                    Status = StagingStatus.Failed,
                    Source = "Info mailbox / failed@example.com",
                    ReceivedAtUtc = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var response = await client.GetAsync($"/api/v1/intake-health?fromUtc={Uri.EscapeDataString(from.ToString("O"))}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(root.GetProperty("evidenceEmails").GetInt32() >= 1);
        Assert.True(root.GetProperty("orderRecords").GetInt32() >= 3);
        Assert.True(root.GetProperty("pendingReview").GetInt32() >= 1);
        Assert.True(root.GetProperty("mappingExceptions").GetInt32() >= 1);
        Assert.True(root.GetProperty("fastPathOrders").GetInt32() >= 1);
        Assert.True(root.GetProperty("failed").GetInt32() >= 1);
        Assert.False(root.GetProperty("healthy").GetBoolean());
    }

    [Fact]
    public async Task Public_intake_probe_exposes_only_sanitised_counts_and_timestamps()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var receivedAt = DateTimeOffset.UtcNow;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.AddRange(
                new StagedImport
                {
                    EntityType = "email-evidence",
                    IdempotencyKey = $"public-health-evidence-{suffix}",
                    PayloadJson = "{\"subject\":\"must-not-leak\",\"sender\":\"private@example.com\"}",
                    Status = StagingStatus.Archived,
                    Source = "Info mailbox evidence / private@example.com",
                    ReceivedAtUtc = receivedAt
                },
                new StagedImport
                {
                    EntityType = "order",
                    IdempotencyKey = $"public-health-order-{suffix}",
                    PayloadJson = "{\"poNumber\":\"SECRET-PO\"}",
                    Status = StagingStatus.PendingReview,
                    Source = "Info mailbox / private@example.com",
                    ReceivedAtUtc = receivedAt
                });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/health/intake");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.True(root.GetProperty("evidenceEmails").GetInt32() >= 1);
        Assert.True(root.GetProperty("orderRecords").GetInt32() >= 1);
        Assert.True(root.TryGetProperty("lastEmailReceivedUtc", out _));
        Assert.True(root.TryGetProperty("lastOrderStagedUtc", out _));
        Assert.DoesNotContain("must-not-leak", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private@example.com", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET-PO", body, StringComparison.OrdinalIgnoreCase);
    }
}
