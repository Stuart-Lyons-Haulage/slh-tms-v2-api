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
/// Drives the real "TSBC - Reynolds" emails through POST /api/v1/order-intake/email,
/// so the intake gates, parser, supersession and staging are all exercised together.
/// </summary>
public sealed class SummerBerryDepotDateIntakeEndToEndTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public SummerBerryDepotDateIntakeEndToEndTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Weekly_Reynolds_email_stages_one_pending_order_per_depot_date()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var messageId = $"e2e-reynolds-weekly-{suffix}";
        var pos = new[] { $"91{suffix[..5]}1", $"91{suffix[..5]}2", $"91{suffix[..5]}3" };

        var response = await PostJson(client, "/api/v1/order-intake/email",
            ReynoldsEmail(messageId, "Antoni.Hristov@summerberry.co.uk", "TSBC - Reynolds", "2026-10-17T08:43:02Z",
                Block("Monday 19th October", pos[0], 2) + Block("Wednesday 21st October", pos[1], 2) + Block("Friday 23rd October", pos[2], 2)));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var staged = await PendingOrdersFor(messageId);
        Assert.Equal(3, staged.Count);
        foreach (var (po, date) in new[] { (pos[0], "2026-10-19"), (pos[1], "2026-10-21"), (pos[2], "2026-10-23") })
        {
            var payload = Assert.Single(staged, item => Text(item, "customerPo") == po);
            Assert.Equal("SUMMERBERRY", Text(payload, "customerCode"));
            Assert.Equal(date, Text(payload, "collectionDate"));
            Assert.Equal(date, Text(payload, "deliveryDate"));
            Assert.Equal(2, payload.GetProperty("pallets").GetInt32());
        }
    }

    [Fact]
    public async Task Revised_list_from_another_Summer_Berry_sender_replaces_the_pending_order()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var original = $"e2e-reynolds-original-{suffix}";
        var revised = $"e2e-reynolds-revised-{suffix}";
        var po = $"92{suffix[..5]}1";

        Assert.Equal(HttpStatusCode.Accepted, (await PostJson(client, "/api/v1/order-intake/email",
            ReynoldsEmail(original, "Antoni.Hristov@summerberry.co.uk", "TSBC - Reynolds", "2026-09-17T09:00:00Z",
                Block("Monday 21st September", po, 2)))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostJson(client, "/api/v1/order-intake/email",
            ReynoldsEmail(revised, "Ioana-Andreea.Pascalau@summerberry.co.uk", "RE: TSBC - Reynolds", "2026-09-18T10:22:08Z",
                "<p>Please see amended - 1 pallet only</p>" + Block("Monday 21st September", po, 1)))).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var rows = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.PayloadJson.Contains(po))
            .ToListAsync();
        var pending = Assert.Single(rows, item => item.Status == StagingStatus.PendingReview);
        using (var document = JsonDocument.Parse(pending.PayloadJson))
        {
            Assert.Equal(revised, Text(document.RootElement, "sourceMessageId"));
            Assert.Equal(1, document.RootElement.GetProperty("pallets").GetInt32());
        }
        Assert.Contains(rows, item => item.Status == StagingStatus.Archived);
    }

    [Fact]
    public async Task Reply_that_only_quotes_the_order_does_not_stage_it_again()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var reply = $"e2e-reynolds-reply-{suffix}";
        var po = $"93{suffix[..5]}1";

        var html = "<p>Hello all ,</p><p>Just to give you an update the next Reynolds collection should be on Friday 02/10 not tomorrow .</p>" +
                   "<p>Regards, </p><div><p><b>From:</b> Antoni Hristov &lt;Antoni.Hristov@summerberry.co.uk&gt;<br /><b>Sent:</b> 29 September 2026 15:12</p></div>" +
                   Block("Wednesday 30th September", po, 4);
        var replyResponse = await PostJson(client, "/api/v1/order-intake/email",
            ReynoldsEmail(reply, "Plamen.Dimitrov@summerberry.co.uk", "RE: TSBC - Reynolds", "2026-09-30T16:16:29Z", html, includeHeading: false));
        Assert.True(replyResponse.IsSuccessStatusCode, $"Intake returned {(int)replyResponse.StatusCode}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.False(await db.StagedImports.AnyAsync(item =>
            item.EntityType == "order" && item.Status == StagingStatus.PendingReview && item.PayloadJson.Contains(po)));
    }

    private static string Block(string date, string po, int pallets) =>
        $"<p><b>{date} depot date</b></p><p>PO {po}<b><br />Total Pallets: {pallets}<br />Collection time: 08:00-09:00</b></p><p><b> </b></p>";

    private static object ReynoldsEmail(string messageId, string sender, string subject, string receivedAtUtc, string blocks, bool includeHeading = true) => new
    {
        messageId,
        internetMessageId = $"<{messageId}@example.test>",
        mailbox = "info@lyonshaulage.com",
        senderAddress = sender,
        senderName = "Summer Berry",
        subject,
        receivedAtUtc = DateTimeOffset.Parse(receivedAtUtc),
        bodyHtml = (includeHeading
                       ? "<p>Good morning,</p><p>Please see below pallet requirements for confirmed orders for next week:</p><p><b>TSBC Reynolds</b></p>"
                       : string.Empty) +
                   blocks +
                   "<p><b>Kind Regards<br />Antoni Hristov</b></p><p>Registered office: Groves Farm, Colworth, Chichester, PO20 2DX</p>",
        webLink = "https://outlook.office.com/mail/e2e"
    };

    private async Task<List<JsonElement>> PendingOrdersFor(string messageId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var rows = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview && item.PayloadJson.Contains(messageId))
            .Select(item => item.PayloadJson)
            .ToListAsync();
        return rows.Select(json => JsonDocument.Parse(json).RootElement.Clone()).ToList();
    }

    private static string? Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<HttpResponseMessage> PostJson(HttpClient client, string url, object payload) =>
        await client.PostAsync(url, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
}
