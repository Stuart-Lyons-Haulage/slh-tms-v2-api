using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

/// <summary>Real attachments from 19 Oct 2026 that were missed, driven through POST /api/v1/order-intake/email.</summary>
public sealed class RealAttachmentIntakeTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public RealAttachmentIntakeTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Vitacress_Waitrose_workbook_with_spaced_heading_stages_four_orders()
    {
        var messageId = $"vitacress-{Guid.NewGuid():N}";
        var response = await Post(messageId, "Milena.Olczak@vitacress.com", "VITACRESS RUNCTON", "2026-10-18T07:59:15Z",
            "<p>Thank you,<br />Milena</p><p>Vitacress Herbs</p>", "Collections WAITROSE.XLS", "application/vnd.ms-excel",
            "Vitacress Collections WAITROSE 19.10.26.xls");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var orders = await OrdersFor(messageId);
        var real = orders.Where(item => !item.Key.EndsWith(":mapping-exception", StringComparison.Ordinal)).Select(item => item.Payload).ToList();
        Assert.Equal(4, real.Count);
        foreach (var (po, pallets) in new[] { ("O79604", 4), ("K79859", 6), ("T79605", 4), ("B79986", 2) })
        {
            var order = Assert.Single(real, item => (item.GetProperty("customerPo").GetString() ?? item.GetProperty("poNumber").GetString() ?? string.Empty).Contains(po, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(pallets, order.GetProperty("pallets").GetInt32());
            Assert.Equal("2026-10-19", order.GetProperty("collectionDate").GetString());
        }
        Assert.DoesNotContain(orders, item => item.Key.EndsWith(":mapping-exception", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nwf_transport_purchase_order_is_not_an_order_card()
    {
        var messageId = $"nwf-po-{Guid.NewGuid():N}";
        var response = await Post(messageId, "ShiftLogisticalPlanner@nwfltd.co.uk", "NWF SLH Outbound PO 18/10", "2026-10-19T06:33:38Z",
            "<p>Kamil Wilkowski<br />Supply Chain Planner<br />Natures Way Foods Ltd</p>", "SLH Outbound PO.pdf", "application/pdf",
            "NWF SLH Outbound PO 19.10.26.pdf");
        Assert.True(response.IsSuccessStatusCode);
        Assert.Empty(await OrdersFor(messageId));
    }

    [Fact]
    public async Task Nwf_crate_collection_sheet_stages_southbound_tray_and_crate_collections()
    {
        var messageId = $"nwf-crates-{Guid.NewGuid():N}";
        var response = await Post(messageId, "D_CrateControl@nwfltd.co.uk", "SLH collection sheet 19.10.26", "2026-10-19T06:00:00Z",
            "<p>Please see attached.</p>", "SLH Collection Sheet 19.10.26.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "NWF SLH Collection Sheet 19.10.26.xlsx");
        Assert.True(response.IsSuccessStatusCode);

        var orders = (await OrdersFor(messageId)).Select(item => item.Payload).ToList();
        var ifco = Assert.Single(orders, item => (item.GetProperty("customerPo").GetString() ?? string.Empty).StartsWith("PO00508879", StringComparison.Ordinal));
        Assert.Equal(26, ifco.GetProperty("pallets").GetInt32());
        Assert.Equal("2026-10-19", ifco.GetProperty("collectionDate").GetString());
        Assert.Equal("Runcton", ifco.GetProperty("stallNumber").GetString());
        Assert.Equal("Trays", ifco.GetProperty("loadUnitType").GetString());
        Assert.Equal("Southbound collections", ifco.GetProperty("planningGroup").GetString());

        var aldi = orders.Where(item => item.GetProperty("customerCode").GetString() == "ALDI").ToList();
        Assert.NotEmpty(aldi);
        Assert.All(aldi, item => Assert.Equal("Euro Pool Bedford", item.GetProperty("sellerName").GetString()));
        Assert.Contains(orders, item => item.GetProperty("loadUnitType").GetString() == "Crates");
    }

    [Fact]
    public async Task Barfoots_euro_pool_return_pdf_splits_by_farm_from_the_email_body()
    {
        var messageId = $"barfoots-return-{Guid.NewGuid():N}";
        var response = await Post(messageId, "Grazyna.Zalewska@barfoots.co.uk",
            "Return dirty Europool trays on 20.10 from Leythorne and Funtington to Bedford depot, 0332403646 20.10.2026", "2026-10-19T09:44:00Z",
            "<p>Hi,</p><p>Please collect the trays and take them to Bedford, 21 pallets from Leythorne and 12 from Funtington.</p><p>Thanks</p>",
            "Order confirmation 0332403646 20.10.2026.PDF", "application/pdf", "Barfoots Euro Pool Return 0332403646 20.10.2026.pdf");
        Assert.True(response.IsSuccessStatusCode);

        var orders = await OrdersFor(messageId);
        Assert.DoesNotContain(orders, item => item.Key.EndsWith(":mapping-exception", StringComparison.Ordinal));
        var payloads = orders.Select(item => item.Payload).ToList();
        Assert.Equal(2, payloads.Count);
        var leythorne = Assert.Single(payloads, item => item.GetProperty("sellerName").GetString() == "Barfoots Leythorne");
        var funtington = Assert.Single(payloads, item => item.GetProperty("sellerName").GetString() == "Barfoots Funtington");
        Assert.Equal(21, leythorne.GetProperty("pallets").GetInt32());
        Assert.Equal(12, funtington.GetProperty("pallets").GetInt32());
        Assert.All(payloads, item =>
        {
            Assert.Equal("BARFOOTS", item.GetProperty("customerCode").GetString());
            Assert.Equal("Bedford", item.GetProperty("stallNumber").GetString());
            Assert.Equal("2026-10-20", item.GetProperty("collectionDate").GetString());
            Assert.Equal("2026-10-20", item.GetProperty("deliveryDate").GetString());
        });
    }

    [Fact]
    public async Task Barfoots_euro_pool_return_pdf_alone_is_one_order_from_the_pdf()
    {
        var messageId = $"barfoots-return-only-{Guid.NewGuid():N}";
        var response = await Post(messageId, "Grazyna.Zalewska@barfoots.co.uk", "Order confirmation 0332403646", "2026-10-19T09:44:00Z",
            "<p>Please see attached.</p>", "Order confirmation 0332403646 20.10.2026.PDF", "application/pdf", "Barfoots Euro Pool Return 0332403646 20.10.2026.pdf");
        Assert.True(response.IsSuccessStatusCode);
        var order = Assert.Single(await OrdersFor(messageId), item => !item.Key.EndsWith(":mapping-exception", StringComparison.Ordinal)).Payload;
        Assert.Equal(33, order.GetProperty("pallets").GetInt32());
        Assert.Equal("Barfoots Sefter", order.GetProperty("sellerName").GetString());
        Assert.Equal("Bedford", order.GetProperty("stallNumber").GetString());
        Assert.Contains("332403646", order.GetProperty("customerPo").GetString());
    }

    [Fact]
    public async Task Barfoots_subject_route_with_pallets_in_body_is_an_order()
    {
        var messageId = $"barfoots-move-{Guid.NewGuid():N}";
        var response = await PostBody(messageId, "Kamila.Biohn@barfoots.co.uk",
            "Collection rental trays from Sefter to Curdworth F00181085 19.10", "2026-10-16T13:24:00Z",
            "<p>Hi,</p><p>Please collect 26 pallets of rental trays from Sefter and deliver to Curdworth on 19th.</p><p>Kind regards</p>");
        Assert.True(response.IsSuccessStatusCode);
        var order = Assert.Single(await OrdersFor(messageId), item => !item.Key.EndsWith(":mapping-exception", StringComparison.Ordinal)).Payload;
        Assert.Equal(26, order.GetProperty("pallets").GetInt32());
        Assert.Equal("Barfoots Sefter", order.GetProperty("sellerName").GetString());
        Assert.Equal("Curdworth", order.GetProperty("stallNumber").GetString());
        Assert.Equal("2026-10-19", order.GetProperty("collectionDate").GetString());
        Assert.Equal("F00181085", order.GetProperty("customerPo").GetString());
    }

    [Fact]
    public async Task Summer_berry_spinneys_details_in_body_go_through_full_intake()
    {
        var messageId = $"sb-spinneys-{Guid.NewGuid():N}";
        var response = await PostBody(messageId, "Antoni.Hristov@summerberry.co.uk", "20.10.2026 Spinneys/JHF delivery", "2026-10-19T07:44:00Z",
            "<p>Good morning,</p><p>Please see the details below:</p><p>Customer : Spinneys/JHF order</p><p>Depot Date: 20.10.2026</p>" +
            "<p>Collection : 19.10 - 17:00</p><p>Pallets : 7</p><p>Adress of delivery : K&amp;N facility</p>" +
            "<p>Building 1235, Eastern Perimeter Road, London Heathrow Airport, Hounslow, TW6 2SQ</p>" +
            "<p>To be there not later than 8AM on the Depot Date.</p><p>Kind Regards</p>");
        Assert.True(response.IsSuccessStatusCode);
        var orders = await OrdersFor(messageId);
        Assert.DoesNotContain(orders, item => item.Key.EndsWith(":mapping-exception", StringComparison.Ordinal));
        var order = Assert.Single(orders).Payload;
        Assert.Equal(7, order.GetProperty("pallets").GetInt32());
        Assert.Equal("2026-10-19", order.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-10-20", order.GetProperty("deliveryDate").GetString());
        Assert.Equal("K&N facility", order.GetProperty("stallNumber").GetString());
    }

    private async Task<HttpResponseMessage> PostBody(string messageId, string sender, string subject, string receivedAt, string html)
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        return await client.PostAsJsonAsync("/api/v1/order-intake/email", new
        {
            messageId,
            internetMessageId = $"<{messageId}@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = sender,
            senderName = sender,
            subject,
            receivedAtUtc = DateTimeOffset.Parse(receivedAt),
            bodyHtml = html,
            webLink = "https://outlook.office.com/mail/test",
            attachments = Array.Empty<object>()
        });
    }

    private async Task<HttpResponseMessage> Post(string messageId, string sender, string subject, string receivedAt, string html, string name, string type, string fixture)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        return await client.PostAsJsonAsync("/api/v1/order-intake/email", new
        {
            messageId,
            internetMessageId = $"<{messageId}@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = sender,
            senderName = sender,
            subject,
            receivedAtUtc = DateTimeOffset.Parse(receivedAt),
            bodyHtml = html,
            webLink = "https://outlook.office.com/mail/test",
            attachments = new[] { new { name, contentType = type, contentBase64 = Convert.ToBase64String(bytes), isInline = false, size = bytes.Length } }
        });
    }

    private async Task<List<(string Key, JsonElement Payload)>> OrdersFor(string messageId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var rows = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview)
            .ToListAsync();
        return rows.Where(item => item.PayloadJson.Contains(messageId, StringComparison.Ordinal))
            .Select(item => (item.IdempotencyKey, JsonDocument.Parse(item.PayloadJson).RootElement.Clone()))
            .ToList();
    }
}
