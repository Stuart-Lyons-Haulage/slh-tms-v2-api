using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class NwfCrateReferenceLinkingEndToEndTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public NwfCrateReferenceLinkingEndToEndTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Repair_endpoint_enriches_a_crate_load_that_was_already_waiting_for_review()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var destination = $"Vitacress Herbs {suffix}";
        var collection = $"Ocado {suffix}";
        var reference = $"229{suffix}";
        Guid targetId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "order",
                IdempotencyKey = $"nwf-existing-dump-{suffix}",
                Status = StagingStatus.PendingReview,
                Source = "NWF crate/tray dump",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    customerCode = "NWF", collectionDate = "2026-09-11", deliveryDate = "2026-09-11",
                    pallets = 18, sellerName = collection, stallNumber = destination,
                    jobType = "NWF crate return", collectionReference = reference
                })
            });
            var target = new StagedImport
            {
                EntityType = "order",
                IdempotencyKey = $"nwf-existing-load-{suffix}",
                Status = StagingStatus.PendingReview,
                Source = "Email",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    customerCode = "NWF", collectionDate = "2026-09-11", deliveryDate = "2026-09-11",
                    pallets = 18, stallNumber = destination, jobType = "IFCO trays",
                    driverInstructions = "Intake warning: Tray/crate reference is missing for the driver text.",
                    intakeWarnings = new[] { "Tray/crate reference is missing for the driver text.", "Collection site was not explicit in the email." }
                })
            };
            db.StagedImports.Add(target);
            await db.SaveChangesAsync();
            targetId = target.Id;
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var response = await PostJson(client, "/api/v1/staging/orders/repair-nwf-references", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var repaired = await verifyDb.StagedImports.SingleAsync(row => row.Id == targetId);
        using var repairedPayload = JsonDocument.Parse(repaired.PayloadJson);
        Assert.Equal(reference, repairedPayload.RootElement.GetProperty("customerRef").GetString());
        Assert.Equal(collection, repairedPayload.RootElement.GetProperty("sellerName").GetString());
        Assert.Contains($"Collection ref: {reference}", repairedPayload.RootElement.GetProperty("driverInstructions").GetString());
        Assert.True(repairedPayload.RootElement.GetProperty("plannerReady").GetBoolean());
        Assert.Empty(repairedPayload.RootElement.GetProperty("intakeWarnings").EnumerateArray());
    }

    [Fact]
    public async Task Later_non_lane_crate_load_is_retained_as_evidence_without_creating_an_order()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var destination = $"Vitacress Herbs {suffix}";
        var collection = $"Ocado {suffix}";
        var reference = $"228{suffix}";
        var messageId = $"crate-load-{suffix}";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "order",
                IdempotencyKey = $"nwf-dump-{suffix}",
                Status = StagingStatus.PendingReview,
                Source = "NWF crate/tray dump",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    poNumber = $"NWF-EQUIP-{reference}",
                    customerCode = "NWF",
                    collectionDate = "2026-09-10",
                    deliveryDate = "2026-09-10",
                    pallets = 18,
                    sellerName = collection,
                    stallNumber = destination,
                    jobType = "NWF crate return",
                    collectionReference = reference,
                    driverInstructions = $"NWF crate return · Collection ref: {reference}"
                })
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var response = await PostJson(client, "/api/v1/order-intake/email", new
        {
            messageId,
            mailbox = "info@lyonshaulage.com",
            senderAddress = "orders@ocado.com",
            subject = $"Ocado tray return {destination} 10/09/2026",
            receivedAtUtc = "2026-09-08T09:30:00Z",
            bodyText = $"IFCO | TBC | | 10/09/2026 | 10/09/2026 | | TBC | TBC | TBC | {destination} | 18 | trays"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"ignored\":true", await response.Content.ReadAsStringAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            Assert.Empty(await db.StagedImports.Where(row => row.EntityType == "order" && row.PayloadJson.Contains(messageId)).ToListAsync());
            Assert.Single(await db.StagedImports.Where(row => row.EntityType == "email-evidence" && row.PayloadJson.Contains(messageId)).ToListAsync());
        }
    }

    private static Task<HttpResponseMessage> PostJson(HttpClient client, string url, object payload) =>
        client.PostAsync(url, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
}
