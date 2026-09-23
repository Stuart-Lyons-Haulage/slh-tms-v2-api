using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class StagingAuditHistoryTests : IClassFixture<CustomWebFactory>
{
    private const string Planner = "planner@lyonshaulage.com";
    private readonly CustomWebFactory factory;

    public StagingAuditHistoryTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Amendment_retains_original_and_new_payload_in_append_only_history()
    {
        // Production defect caught: correcting an extracted order overwrites
        // PayloadJson and makes the originally received values unrecoverable.
        var client = factory.CreateClientWithUser(Planner, "Tms.Approve");
        var id = await StageOrder(client, "history-amend", 12);

        var amended = Json("""
            {"payload":{"poNumber":"PO-HISTORY-AMEND","customerCode":"COOP","collectionDate":"2026-08-24","pallets":18},"note":"Corrected against source attachment"}
            """);
        var amendmentResponse = await client.PutAsync($"/api/v1/staging/{id}/payload", amended);
        Assert.Equal(HttpStatusCode.OK, amendmentResponse.StatusCode);

        var historyResponse = await client.GetAsync($"/api/v1/staging/{id}/history");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        using var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        var events = history.RootElement.EnumerateArray().ToList();
        Assert.Equal(new[] { "Received", "Amended" }, events.Select(x => x.GetProperty("eventType").GetString()));
        Assert.Equal(12, events[0].GetProperty("payload").GetProperty("pallets").GetInt32());
        Assert.Equal(18, events[1].GetProperty("payload").GetProperty("pallets").GetInt32());
    }

    [Fact]
    public async Task Archive_pending_retains_record_and_writes_history_event()
    {
        // Production defect caught: the legacy clear-pending operation performs
        // a hard SQL delete, removing the order and its audit evidence.
        var client = factory.CreateClientWithUser(Planner, "Tms.Approve");
        var id = await StageOrder(client, "history-archive", 7);

        var archiveResponse = await client.PostAsync(
            "/api/v1/staging/pending/archive",
            Json("{\"reason\":\"Controlled test archive\"}"));
        Assert.Equal(HttpStatusCode.OK, archiveResponse.StatusCode);

        var stagedResponse = await client.GetAsync($"/api/v1/staging/{id}");
        using var staged = JsonDocument.Parse(await stagedResponse.Content.ReadAsStringAsync());
        Assert.Equal("Archived", staged.RootElement.GetProperty("status").GetString());

        var historyResponse = await client.GetAsync($"/api/v1/staging/{id}/history");
        using var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        Assert.Contains(history.RootElement.EnumerateArray(), x => x.GetProperty("eventType").GetString() == "Archived");
    }

    [Fact]
    public async Task Approval_links_live_order_to_staged_source_and_records_decision_events()
    {
        // Production defect caught: a promoted TransportOrder can currently be
        // found only by matching its reference back to JSON, not by a durable FK.
        var client = factory.CreateClientWithUser(Planner, "Tms.Approve");
        var id = await StageOrder(client, "history-promote", 21);

        var approvalResponse = await client.PostAsync(
            $"/api/v1/staging/{id}/approve",
            Json("{\"note\":\"Planner checked source evidence\"}"));
        Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);

        var ordersResponse = await client.GetAsync("/api/v1/orders");
        Assert.Equal(HttpStatusCode.OK, ordersResponse.StatusCode);
        using var orders = JsonDocument.Parse(await ordersResponse.Content.ReadAsStringAsync());
        var order = Assert.Single(orders.RootElement.EnumerateArray().Where(x => x.GetProperty("reference").GetString() == "PO-HISTORY-PROMOTE"));
        Assert.Equal(id, order.GetProperty("sourceStagedImportId").GetGuid());

        var historyResponse = await client.GetAsync($"/api/v1/staging/{id}/history");
        using var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            new[] { "Received", "Approved", "Promoted" },
            history.RootElement.EnumerateArray().Select(x => x.GetProperty("eventType").GetString()));
    }

    [Fact]
    public async Task Reject_is_idempotent_and_preserves_the_original_audit_evidence()
    {
        var client = factory.CreateClientWithUser(Planner, "Tms.Approve");
        var id = await StageOrder(client, "reject-idempotent", 4);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/staging/{id}/reject", Json("{\"note\":\"Not a transport order\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/staging/{id}/reject", Json("{\"note\":\"stale retry\"}"))).StatusCode);

        using var history = JsonDocument.Parse(await (await client.GetAsync($"/api/v1/staging/{id}/history")).Content.ReadAsStringAsync());
        Assert.Equal(new[] { "Received", "Rejected" }, history.RootElement.EnumerateArray().Select(x => x.GetProperty("eventType").GetString()));
        Assert.Equal("Not a transport order", history.RootElement[1].GetProperty("note").GetString());
    }

    [Fact]
    public async Task Reject_of_an_already_approved_row_returns_conflict_instead_of_500()
    {
        var id = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Slh.Tms.Api.Data.TmsDbContext>();
            db.StagedImports.Add(new StagedImport
            {
                Id = id,
                EntityType = "order",
                IdempotencyKey = "reject-approved",
                PayloadJson = "{}",
                Status = StagingStatus.Approved,
                Source = "test"
            });
            await db.SaveChangesAsync();
        }

        var response = await factory.CreateClientWithUser(Planner, "Tms.Approve").PostAsync($"/api/v1/staging/{id}/reject", Json("{\"note\":\"stale\"}"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("staging_already_reviewed", body.RootElement.GetProperty("code").GetString());
    }

    private static async Task<Guid> StageOrder(HttpClient client, string key, int pallets)
    {
        var reference = $"PO-{key.ToUpperInvariant()}";
        var request = JsonSerializer.Serialize(new
        {
            entityType = "order",
            idempotencyKey = key,
            source = "PowerAutomate/InfoMailbox",
            payload = new
            {
                poNumber = reference,
                customerCode = "COOP",
                collectionDate = "2026-08-24",
                deliveryDate = "2026-08-24",
                pallets
            }
        });
        var response = await client.PostAsync("/api/v1/staging", Json(request));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("stagingId").GetGuid();
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}
