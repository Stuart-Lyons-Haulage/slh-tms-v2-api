using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OutstandingReferenceTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public OutstandingReferenceTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Missing_load_reference_remains_visible_after_delivery_and_draft_never_sends()
    {
        var movementId = await SeedDeliveredMovement();
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");

        var listResponse = await client.GetAsync("/api/v1/outstanding-references");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var row = Assert.Single(list.RootElement.GetProperty("references").EnumerateArray().Where(x => x.GetProperty("movementId").GetGuid() == movementId));
        Assert.Equal("LoadReference", row.GetProperty("referenceType").GetString());

        var draftResponse = await client.PostAsync($"/api/v1/outstanding-references/{row.GetProperty("id").GetGuid()}/draft-chase", null);
        Assert.Equal(HttpStatusCode.OK, draftResponse.StatusCode);
        using var draft = JsonDocument.Parse(await draftResponse.Content.ReadAsStringAsync());
        Assert.Equal("orders@customer.example", draft.RootElement.GetProperty("to").GetString());
        Assert.Equal("THREAD-1", draft.RootElement.GetProperty("conversationId").GetString());
        Assert.True(draft.RootElement.GetProperty("requiresReview").GetBoolean());
        Assert.False(draft.RootElement.GetProperty("sendsAutomatically").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Empty(db.ReferenceChaseEvents.Where(x => x.ReferenceIssueId == row.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Sent_and_resolution_metadata_is_retained()
    {
        var movementId = await SeedDeliveredMovement();
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        using var list = JsonDocument.Parse(await (await client.GetAsync("/api/v1/outstanding-references")).Content.ReadAsStringAsync());
        var issueId = Assert.Single(list.RootElement.GetProperty("references").EnumerateArray().Where(x => x.GetProperty("movementId").GetGuid() == movementId)).GetProperty("id").GetGuid();

        var sent = await client.PostAsync($"/api/v1/outstanding-references/{issueId}/record-sent", Json("""{"recipient":"orders@customer.example","providerMessageId":"OUTLOOK-1","providerThreadId":"THREAD-1","note":"Reviewed by planner"}"""));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var resolved = await client.PostAsync($"/api/v1/outstanding-references/{issueId}/resolve", Json("""{"referenceValue":"LOAD-777","note":"Customer replied"}"""));
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var issue = db.OrderReferenceIssues.Single(x => x.Id == issueId);
        Assert.Equal(ReferenceIssueStatus.Resolved, issue.Status);
        Assert.Contains("LOAD-777", issue.Notes);
        Assert.Contains(db.ReferenceChaseEvents, x => x.ReferenceIssueId == issueId && x.EventType == "Sent" && x.ProviderMessageId == "OUTLOOK-1");
        Assert.Contains(db.ReferenceChaseEvents, x => x.ReferenceIssueId == issueId && x.EventType == "Resolved");
    }

    private async Task<Guid> SeedDeliveredMovement()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var staged = new StagedImport { EntityType = "order", IdempotencyKey = $"ref-{Guid.NewGuid():N}", Status = StagingStatus.Promoted, PayloadJson = "{}" };
        var movement = new OrderMovement { CustomerCode = "COOP", StableMovementKey = $"COOP:REF:{Guid.NewGuid():N}", LifecycleStatus = OrderMovementStatus.PlannerReady };
        var revision = new OrderRevision { MovementId = movement.Id, StagedImportId = staged.Id, RevisionNumber = 1, MessageId = "MSG-1", PayloadJson = """{"poNumber":"PO-1","sourceSender":"orders@customer.example","sourceEmailSubject":"Order PO-1","sourceEmailMessageId":"MSG-1","sourceConversationId":"THREAD-1"}""" };
        movement.CurrentRevisionId = revision.Id;
        var order = new TransportOrder { SourceStagedImportId = staged.Id, SourceMovementId = movement.Id, Reference = ($"PO-{Guid.NewGuid():N}")[..20], CustomerCode = "COOP", CollectionDate = new DateOnly(2026, 8, 24), Status = OrderStatus.Delivered };
        db.AddRange(staged, movement, revision, new OrderSourceLine { RevisionId = revision.Id, SourceRowKey = "1", Pallets = 12, CollectionSite = "Farm", DeliverySite = "COOP", PayloadJson = "{}" }, order);
        await db.SaveChangesAsync();
        return movement.Id;
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}
