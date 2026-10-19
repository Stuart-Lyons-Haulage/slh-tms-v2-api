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
