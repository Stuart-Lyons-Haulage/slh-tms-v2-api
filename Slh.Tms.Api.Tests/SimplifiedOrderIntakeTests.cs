using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class VerifiedFormatOrderIntakeTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public VerifiedFormatOrderIntakeTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Nwf_morrisons_structured_order_enters_pending_review()
    {
        var messageId = $"simplified-nwf-morrisons-{Guid.NewGuid():N}";
        const string body = """
Haulier Name| Requested Ship Date| 04. Collection Site| Customer Name| DepotID| Depot Description| Delivery Address| Sales Order ID| CustomerRef| Pallet Name| PalletQty| PO REF
---|---|---|---|---|---|---|---|---|---|---|---
Stuart Lyons| 20/09/2026| Selsey| Morrisons| MOR09| Morrisons FRUITSITTINGBOURNE 763| ME10 2FD| SO000999001| 91329634| IPP STD| 10| PO00999001
Stuart Lyons| 20/09/2026| Selsey| NISA| NISA01| NISA depot| UK| SO000999099| REF99| IPP STD| 4| PO00999099
""";

        var response = await Post(new
        {
            messageId,
            mailbox = "info@lyonshaulage.com",
            senderAddress = "ShiftLogisticalPlanner@nwfltd.co.uk",
            senderName = "Shift Logistical Planner",
            subject = "NWAY Stuart Lyons Transport Pallet Order Report 20/09/2026",
            receivedAtUtc = "2026-09-19T05:37:05Z",
            bodyText = body,
            attachments = new[] { new { name = "NWAY Pallet Order 20-09-2026.csv", contentType = "text/csv", isInline = false, size = 8192 } }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var orders = db.StagedImports.Where(item => item.EntityType == "order" && item.PayloadJson.Contains(messageId)).ToList();
        Assert.Equal(2, orders.Count);
        Assert.All(orders, order => Assert.Equal(StagingStatus.PendingReview, order.Status));
        var payloads = orders.Select(order => JsonDocument.Parse(order.PayloadJson)).ToList();
        try
        {
            Assert.All(payloads, payload => Assert.Equal("Selsey", payload.RootElement.GetProperty("sellerName").GetString()));
            Assert.Contains(payloads, payload => string.Equals(payload.RootElement.GetProperty("customerName").GetString(), "Morrisons", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(payloads, payload => string.Equals(payload.RootElement.GetProperty("customerName").GetString(), "NISA", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var payload in payloads) payload.Dispose();
        }
    }

    [Fact]
    public async Task Nwf_confirmed_aldi_20_september_splits_into_three_pending_review_movements()
    {
        var messageId = $"simplified-nwf-aldi-confirmed-{Guid.NewGuid():N}";
        const string body = """
Please see confirmed ALDI trays collection for 19/09

ALDI| PO00505088 | £195.87| 19/09/2026| 20/09/2026| 26| Bedford| 228419235| PO00503669 | Merston / Runcton| 33| Merston 18 plt / Runcton 15/ plt
ALDI| PO00505089 | £195.87| 19/09/2026| 20/09/2026| 26| Bedford| 228419486| PO00503677 | Selsey| 33|
""";

        var response = await Post(new
        {
            messageId,
            mailbox = "info@lyonshaulage.com",
            senderAddress = "MariuszUrbanski@nwfltd.co.uk",
            senderName = "Mariusz Urbanski",
            subject = "Aldi Bedford - confirmation for 19/09",
            receivedAtUtc = "2026-09-17T13:23:55Z",
            bodyText = body
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var orders = db.StagedImports.Where(item => item.EntityType == "order" && item.PayloadJson.Contains(messageId)).ToList();
        Assert.Equal(3, orders.Count);
        Assert.All(orders, order => Assert.Equal(StagingStatus.PendingReview, order.Status));
        var payloads = orders.Select(order => JsonDocument.Parse(order.PayloadJson)).ToList();
        try
        {
            Assert.Equal(66, payloads.Sum(payload => payload.RootElement.GetProperty("pallets").GetInt32()));
            Assert.Contains(payloads, payload => payload.RootElement.GetProperty("sellerName").GetString() == "Merston" && payload.RootElement.GetProperty("pallets").GetInt32() == 18);
            Assert.Contains(payloads, payload => payload.RootElement.GetProperty("sellerName").GetString() == "Runcton" && payload.RootElement.GetProperty("pallets").GetInt32() == 15);
            Assert.Contains(payloads, payload => payload.RootElement.GetProperty("sellerName").GetString() == "Selsey" && payload.RootElement.GetProperty("pallets").GetInt32() == 33);
            Assert.All(payloads, payload => Assert.Equal("2026-09-20", payload.RootElement.GetProperty("deliveryDate").GetString()));
        }
        finally
        {
            foreach (var payload in payloads) payload.Dispose();
        }
    }

    [Fact]
    public async Task Nwf_verified_table_accepts_nisa_as_a_valid_customer()
    {
        var messageId = $"verified-nwf-nisa-{Guid.NewGuid():N}";
        const string body = """
Haulier Name| Requested Ship Date| 04. Collection Site| Customer Name| DepotID| Depot Description| Delivery Address| Sales Order ID| CustomerRef| Pallet Name| PalletQty| PO REF
---|---|---|---|---|---|---|---|---|---|---|---
Stuart Lyons| 20/09/2026| Selsey| NISA| NISA01| NISA depot| UK| SO000999002| REF1| IPP STD| 4| PO00999002
""";

        var response = await Post(new
        {
            messageId,
            mailbox = "info@lyonshaulage.com",
            senderAddress = "ShiftLogisticalPlanner@nwfltd.co.uk",
            senderName = "Shift Logistical Planner",
            subject = "NWAY Stuart Lyons Transport Pallet Order Report 20/09/2026",
            receivedAtUtc = "2026-09-19T05:38:05Z",
            bodyText = body
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var order = Assert.Single(db.StagedImports.Where(item => item.EntityType == "order" && item.PayloadJson.Contains(messageId)));
        Assert.Equal(StagingStatus.PendingReview, order.Status);
        using var payload = JsonDocument.Parse(order.PayloadJson);
        Assert.Equal("NWF", payload.RootElement.GetProperty("customerCode").GetString());
        Assert.Equal("NISA", payload.RootElement.GetProperty("customerName").GetString());
        Assert.Equal(4, payload.RootElement.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public async Task Unknown_order_like_email_is_evidence_only_without_sender_route_or_generic_guessing()
    {
        var messageId = $"verified-unknown-{Guid.NewGuid():N}";
        var response = await Post(new
        {
            messageId,
            mailbox = "info@lyonshaulage.com",
            senderAddress = "orders@unknown-customer.example",
            senderName = "Unknown Customer",
            subject = "Order for 20/09",
            receivedAtUtc = "2026-09-19T08:00:00Z",
            bodyText = "Please collect 12 pallets from Selsey and deliver to Birmingham on 20/09/2026. PO 12345."
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ignored\":true", responseBody);
        Assert.Contains("No verified order format matched", responseBody);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Empty(db.StagedImports.Where(item => item.EntityType == "order" && item.PayloadJson.Contains(messageId)));
        Assert.Single(db.StagedImports.Where(item => item.EntityType == "email-evidence" && item.PayloadJson.Contains(messageId)));
    }

    [Theory]
    [InlineData("PackagingPlanner@nwfltd.co.uk", "NWF transfer - Barnham to Drayton SUN 20/09", "@D_Drayton Logistics - please receive on arrival = INTO000173010\n20/09/2026 | 25FPPCOLTOV2 | V1 | 35,840 | 2plts = All stock", "Barnham", "Drayton", 2)]
    [InlineData("gerone@lyonshaulage.com", "Merston to Drayton transfers for collections - 20-09-2026", "Please could you transfer pallets from Merston to Drayton:\n10 Aldi-Cardiff", "Merston", "Drayton", null)]
    public async Task Nwf_transfer_enters_pending_review_even_without_target_retailer(
        string senderAddress,
        string subject,
        string body,
        string expectedCollection,
        string expectedDestination,
        int? expectedPallets)
    {
        var messageId = $"simplified-nwf-transfer-{Guid.NewGuid():N}";
        var response = await Post(new
        {
            messageId,
            mailbox = "info@lyonshaulage.com",
            senderAddress,
            senderName = "NWF transfer planner",
            subject,
            receivedAtUtc = "2026-09-18T07:04:36Z",
            bodyText = body
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var order = Assert.Single(db.StagedImports.Where(item => item.EntityType == "order" && item.PayloadJson.Contains(messageId)));
        Assert.Equal(StagingStatus.PendingReview, order.Status);
        using var payload = JsonDocument.Parse(order.PayloadJson);
        Assert.Equal("NWF", payload.RootElement.GetProperty("customerCode").GetString());
        Assert.Equal("Collection transfer", payload.RootElement.GetProperty("jobType").GetString());
        Assert.Equal(expectedCollection, payload.RootElement.GetProperty("sellerName").GetString());
        Assert.Equal(expectedDestination, payload.RootElement.GetProperty("stallNumber").GetString());
        if (expectedPallets is null)
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("pallets").ValueKind);
        else
            Assert.Equal(expectedPallets.Value, payload.RootElement.GetProperty("pallets").GetInt32());
    }

    private Task<HttpResponseMessage> Post(object payload)
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        return client.PostAsync("/api/v1/order-intake/email", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
    }
}
