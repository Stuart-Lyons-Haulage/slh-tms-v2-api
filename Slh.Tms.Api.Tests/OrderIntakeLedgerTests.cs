using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeLedgerTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public OrderIntakeLedgerTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Ledger_returns_message_attachment_lifecycle_and_pallet_totals()
    {
        var (stagedId, movementId) = await Seed();
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com");

        var response = await client.GetAsync("/api/v1/order-intake/ledger?take=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(body.RootElement.GetProperty("records").EnumerateArray()
            .Where(x => x.GetProperty("stagedImportId").GetGuid() == stagedId));
        Assert.Equal(stagedId, row.GetProperty("stagedImportId").GetGuid());
        Assert.Equal(movementId, row.GetProperty("movementId").GetGuid());
        Assert.Equal("MSG-LEDGER", row.GetProperty("messageId").GetString());
        Assert.Equal("orders.xlsx", row.GetProperty("attachmentIdentity").GetString());
        Assert.Equal(20, row.GetProperty("palletsReceived").GetInt32());
        Assert.Equal("PlannerReady", row.GetProperty("lifecycleStatus").GetString());
    }

    [Fact]
    public async Task Replay_is_idempotent_and_audited_without_cloning_staging()
    {
        var (stagedId, movementId) = await Seed();
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");

        var response = await client.PostAsync($"/api/v1/order-intake/ledger/{stagedId}/replay", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(stagedId, body.RootElement.GetProperty("stagedImportId").GetGuid());
        Assert.Equal(movementId, body.RootElement.GetProperty("movementId").GetGuid());
        Assert.True(body.RootElement.GetProperty("idempotent").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Single(db.StagedImports.Where(x => x.Id == stagedId));
        Assert.Contains(db.StagedImportEvents, x => x.StagedImportId == stagedId && x.EventType == "ReplayRequested");
        Assert.Contains(db.StagedImportEvents, x => x.StagedImportId == stagedId && x.EventType == "ReplayCompleted");
    }

    private async Task<(Guid StagedId, Guid MovementId)> Seed()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var staged = new StagedImport
        {
            EntityType = "order", IdempotencyKey = $"ledger-{Guid.NewGuid():N}", Status = StagingStatus.PendingReview,
            PayloadJson = """{"customerCode":"COOP","poNumber":"PO-LEDGER","sourceEmailMessageId":"MSG-LEDGER","sourceEmailSubject":"Order","sourceSender":"orders@example.com"}"""
        };
        var movement = new OrderMovement { CustomerCode = "COOP", StableMovementKey = $"COOP:POLEDGER:{Guid.NewGuid():N}", LifecycleStatus = OrderMovementStatus.PlannerReady };
        var revision = new OrderRevision { MovementId = movement.Id, StagedImportId = staged.Id, RevisionNumber = 1, MessageId = "MSG-LEDGER", AttachmentIdentity = "orders.xlsx", ParserTemplate = "COOP", ParserVersion = "2", PayloadJson = staged.PayloadJson };
        movement.CurrentRevisionId = revision.Id;
        db.AddRange(staged, movement, revision,
            new OrderSourceLine { RevisionId = revision.Id, SourceRowKey = "1", CollectionSite = "Farm A", DeliverySite = "COOP", Pallets = 12, PayloadJson = "{}" },
            new OrderSourceLine { RevisionId = revision.Id, SourceRowKey = "2", CollectionSite = "Farm B", DeliverySite = "COOP", Pallets = 8, PayloadJson = "{}" });
        await db.SaveChangesAsync();
        return (staged.Id, movement.Id);
    }
}
