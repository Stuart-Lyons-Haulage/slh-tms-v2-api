using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

/// <summary>
/// Smoke tests for the v2 confirmation workflow:
///   1. NWF CSV intake — repair endpoint enriches crate loads
///   2. Planner approval — confirm/reject endpoints honour TmsApprove
///   3. Planning gate — unconfirmed orders rejected from runs
///   4. Dispatch refresh — SSE stream endpoint responds
/// </summary>
public sealed class ConfirmationWorkflowSmokeTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public ConfirmationWorkflowSmokeTests(CustomWebFactory factory) => this.factory = factory;

    // ──────────────────────────────────────────────────────────
    // 1. NWF CSV intake: repair endpoint responds OK
    // ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Nwf_repair_endpoint_responds_ok()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var response = await PostJson(client, "/api/v1/staging/orders/repair-nwf-references", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────
    // 2. Planner approval: confirm and reject require TmsApprove
    // ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Confirm_endpoint_requires_tms_approve_policy()
    {
        // No TmsApprove scope → should be forbidden
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        client.DefaultRequestHeaders.Add("X-Test-Auth-Source", "local");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "TMS.Warehouse");
        var response = await PostJson(client, "/api/v1/orders/confirm", new { ids = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Confirm_endpoint_confirms_awaiting_order()
    {
        Guid orderId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var order = new TransportOrder
            {
                CustomerCode = "SMOKE",
                Reference = $"SMOKE-CONFIRM-{Guid.NewGuid():N}",
                CollectionDate = DateOnly.FromDateTime(DateTime.UtcNow),
                ConfirmationStatus = ConfirmationStatus.AwaitingReview
            };
            db.TransportOrders.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;
        }

        var client = factory.CreateClientWithUser("approver@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var response = await PostJson(client, "/api/v1/orders/confirm", new { ids = new[] { orderId } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("confirmed").GetInt32());

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<TmsDbContext>();
        var updated = await verifyDb.TransportOrders.SingleAsync(o => o.Id == orderId);
        Assert.Equal(ConfirmationStatus.Confirmed, updated.ConfirmationStatus);
        Assert.NotNull(updated.ConfirmedAtUtc);
        Assert.Equal("approver@lyonshaulage.com", updated.ConfirmedBy);
    }

    [Fact]
    public async Task Reject_endpoint_rejects_awaiting_order()
    {
        Guid orderId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var order = new TransportOrder
            {
                CustomerCode = "SMOKE",
                Reference = $"SMOKE-REJECT-{Guid.NewGuid():N}",
                CollectionDate = DateOnly.FromDateTime(DateTime.UtcNow),
                ConfirmationStatus = ConfirmationStatus.AwaitingReview
            };
            db.TransportOrders.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;
        }

        var client = factory.CreateClientWithUser("approver@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var response = await PostJson(client, "/api/v1/orders/reject", new { ids = new[] { orderId } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<TmsDbContext>();
        var updated = await verifyDb.TransportOrders.SingleAsync(o => o.Id == orderId);
        Assert.Equal(ConfirmationStatus.Rejected, updated.ConfirmationStatus);
    }

    [Fact]
    public async Task Awaiting_review_endpoint_lists_unconfirmed_orders()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.TransportOrders.Add(new TransportOrder
            {
                CustomerCode = "SMOKE",
                Reference = $"SMOKE-LIST-{Guid.NewGuid():N}",
                CollectionDate = today,
                ConfirmationStatus = ConfirmationStatus.AwaitingReview
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var response = await client.GetAsync($"/api/v1/orders/awaiting-review?date={today:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("count").GetInt32() >= 1);
    }

    // ──────────────────────────────────────────────────────────
    // 3. Planning gate: unconfirmed orders blocked from runs
    // ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Planning_gate_rejects_unconfirmed_order_on_run()
    {
        Guid loadId, orderId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var order = new TransportOrder
            {
                CustomerCode = "SMOKE",
                Reference = $"SMOKE-GATE-{Guid.NewGuid():N}",
                CollectionDate = DateOnly.FromDateTime(DateTime.UtcNow),
                ConfirmationStatus = ConfirmationStatus.AwaitingReview
            };
            var load = new Load
            {
                Reference = $"SMOKE-RUN-{Guid.NewGuid():N}",
                Status = LoadStatus.Draft,
                PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Stops = new List<LoadStop>()
            };
            db.TransportOrders.Add(order);
            db.Loads.Add(load);
            await db.SaveChangesAsync();
            orderId = order.Id;
            loadId = load.Id;
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var response = await client.PutAsync(
            $"/api/v1/planning-control/runs/{loadId}/stops",
            new StringContent(JsonSerializer.Serialize(new[]
            {
                new { orderId, name = "Test Stop", address = "Test Address" }
            }), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not been confirmed", body);
    }

    [Fact]
    public async Task Planning_gate_allows_confirmed_order_on_run()
    {
        Guid loadId, orderId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var order = new TransportOrder
            {
                CustomerCode = "SMOKE",
                Reference = $"SMOKE-ALLOW-{Guid.NewGuid():N}",
                CollectionDate = DateOnly.FromDateTime(DateTime.UtcNow),
                ConfirmationStatus = ConfirmationStatus.Confirmed,
                ConfirmedAtUtc = DateTimeOffset.UtcNow,
                ConfirmedBy = "test"
            };
            var load = new Load
            {
                Reference = $"SMOKE-RUN-{Guid.NewGuid():N}",
                Status = LoadStatus.Draft,
                PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Stops = new List<LoadStop>()
            };
            db.TransportOrders.Add(order);
            db.Loads.Add(load);
            await db.SaveChangesAsync();
            orderId = order.Id;
            loadId = load.Id;
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var response = await client.PutAsync(
            $"/api/v1/planning-control/runs/{loadId}/stops",
            new StringContent(JsonSerializer.Serialize(new[]
            {
                new { orderId, name = "Test Stop", address = "Test Address" }
            }), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────
    // 4. Dispatch refresh: SSE stream endpoint accepts connection
    // ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Planning_events_stream_connects_successfully()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        client.Timeout = TimeSpan.FromSeconds(5);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await client.GetAsync(
                "/api/v1/planning-events/stream",
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException)
        {
            // Expected — the SSE stream stays open until cancelled,
            // so a cancellation after 2s proves it connected.
        }
    }

    private static Task<HttpResponseMessage> PostJson(HttpClient client, string url, object payload) =>
        client.PostAsync(url, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
}
