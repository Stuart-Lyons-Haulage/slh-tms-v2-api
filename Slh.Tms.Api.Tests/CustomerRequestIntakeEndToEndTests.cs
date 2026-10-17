using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

/// <summary>
/// Customer replies that carry no order of their own must not become empty
/// "Reference missing" review cards. They are recorded as customer communications
/// and attached to the orders in the same Outlook conversation.
/// Based on real info@ mail: Antoni Hristov (Summer Berry), 17 Oct 2026 14:42,
/// "Would be you able to push back all collections for as late as possible", and
/// Ioana Pascalau, 30 Sep 2026, "Can I please have an ETA for today's delivery?".
/// </summary>
public sealed class CustomerRequestIntakeEndToEndTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public CustomerRequestIntakeEndToEndTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Push_back_reply_is_linked_to_the_threads_orders_not_staged_as_a_card()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var conversation = $"AAQkConversation{suffix}=";
        var orderMessage = $"e2e-thread-order-{suffix}";
        var replyMessage = $"e2e-thread-reply-{suffix}";
        var po = $"94{suffix[..5]}1";

        var order = await PostJson(client, Email(orderMessage, conversation, "Antoni.Hristov@summerberry.co.uk", "Antoni Hristov",
            "TSBC - Reynolds", "<p><b>TSBC Reynolds</b></p>" + Block("Monday 19th October", po, 2) + Signature));
        Assert.True(order.IsSuccessStatusCode);

        var reply = await PostJson(client, Email(replyMessage, conversation, "Antoni.Hristov@summerberry.co.uk", "Antoni Hristov",
            "RE: TSBC - Reynolds",
            "<p>Good afternoon,</p><p>Would be you able to push back all collections for as late as possible.</p><p>Thank you!</p>" + Signature +
            "<div><p><b>From:</b> Antoni Hristov<br /><b>Sent:</b> 17 October 2026 09:42<br /><b>Subject:</b> TSBC - Reynolds</p></div>" +
            "<p><b>TSBC Reynolds</b></p>" + Block("Monday 19th October", po, 2)));
        Assert.True(reply.IsSuccessStatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();

        // No review card of any kind for the reply itself.
        Assert.False(await db.StagedImports.AnyAsync(item =>
            item.EntityType == "order" && item.Status == StagingStatus.PendingReview &&
            item.PayloadJson.Contains("\"sourceMessageId\":\"" + replyMessage + "\"")));

        // Recorded as a customer request.
        var communication = await db.StagedImports.SingleAsync(item => item.EntityType == "communication" && item.IdempotencyKey == $"communication:{replyMessage}");
        using (var document = JsonDocument.Parse(communication.PayloadJson))
        {
            Assert.Equal("Request", document.RootElement.GetProperty("extraction").GetProperty("purpose").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("linkedOrders").GetArrayLength());
        }

        // The pending order in the same thread now shows the request to the planner.
        var pending = await db.StagedImports.SingleAsync(item =>
            item.EntityType == "order" && item.Status == StagingStatus.PendingReview && item.PayloadJson.Contains(orderMessage));
        using (var document = JsonDocument.Parse(pending.PayloadJson))
        {
            var warnings = document.RootElement.GetProperty("intakeWarnings").EnumerateArray().Select(item => item.GetString()).ToList();
            Assert.Contains(warnings, warning => warning!.StartsWith("Customer request", StringComparison.Ordinal) &&
                                                 warning.Contains("push back all collections for as late as possible", StringComparison.OrdinalIgnoreCase) &&
                                                 !warning.Contains("Kind Regards", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, document.RootElement.GetProperty("customerRequests").GetArrayLength());
        }
        Assert.True(await db.StagedImportEvents.AnyAsync(evt => evt.StagedImportId == pending.Id && evt.EventType == "CustomerRequestLinked"));

        // Replaying the same reply does not link it twice.
        Assert.True((await PostJson(client, Email(replyMessage, conversation, "Antoni.Hristov@summerberry.co.uk", "Antoni Hristov",
            "RE: TSBC - Reynolds", "<p>Would be you able to push back all collections for as late as possible.</p>" + Signature))).IsSuccessStatusCode);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Equal(1, await verifyDb.StagedImportEvents.CountAsync(evt => evt.StagedImportId == pending.Id && evt.EventType == "CustomerRequestLinked"));
    }

    [Fact]
    public async Task Eta_chaser_with_no_order_content_is_recorded_not_staged()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var message = $"e2e-eta-chase-{suffix}";

        var response = await PostJson(client, Email(message, $"AAQkEta{suffix}=", "Ioana-Andreea.Pascalau@summerberry.co.uk", "Ioana-Andreea Pascalau",
            "RE: TSBC - Reynolds",
            "<p>Good afternoon,</p><p>Can I please have an ETA for today’s delivery? Raynolds are asking for it, as it has not arrived yet</p><p>Kind regards</p><p>Ioana</p>" +
            "<div><p><b>From:</b> Antoni Hristov<br /><b>Sent:</b> 29 September 2026 15:12</p></div><p>PO 3171459<br />Total Pallets: 4</p>"));
        Assert.True(response.IsSuccessStatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.False(await db.StagedImports.AnyAsync(item => item.EntityType == "order" && item.PayloadJson.Contains(message)));
        var communication = await db.StagedImports.SingleAsync(item => item.EntityType == "communication" && item.IdempotencyKey == $"communication:{message}");
        using var document = JsonDocument.Parse(communication.PayloadJson);
        Assert.Equal("EtaUpdate", document.RootElement.GetProperty("extraction").GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task Customer_email_with_its_own_order_content_still_gets_a_review_card_when_unparsed()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var message = $"e2e-own-evidence-{suffix}";

        // Own text names a PO and pallets in a layout no verified parser reads.
        var response = await PostJson(client, Email(message, $"AAQkOwn{suffix}=", "orders@barfoots.co.uk", "Barfoots",
            "Extra Aldi load", "<p>Hi, extra Aldi load please, PO 77812345, 6 pallets, collect Monday.</p><p>Thanks</p>"));
        Assert.True(response.IsSuccessStatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.True(await db.StagedImports.AnyAsync(item =>
            item.EntityType == "order" && item.Status == StagingStatus.PendingReview &&
            item.PayloadJson.Contains(message) && item.PayloadJson.Contains("MappingException")));
    }

    private const string Signature = "<p><b>Kind Regards<br />Antoni Hristov</b></p><p>Registered office: Groves Farm, Colworth, Chichester, PO20 2DX</p>";

    private static string Block(string date, string po, int pallets) =>
        $"<p><b>{date} depot date</b></p><p>PO {po}<b><br />Total Pallets: {pallets}<br />Collection time: 08:00-09:00</b></p>";

    private static object Email(string messageId, string conversationId, string sender, string senderName, string subject, string html) => new
    {
        messageId,
        internetMessageId = $"<{messageId}@example.test>",
        conversationId,
        mailbox = "info@lyonshaulage.com",
        senderAddress = sender,
        senderName,
        subject,
        receivedAtUtc = DateTimeOffset.Parse("2026-10-17T13:42:48Z"),
        bodyHtml = html,
        webLink = "https://outlook.office.com/mail/e2e"
    };

    private static async Task<HttpResponseMessage> PostJson(HttpClient client, object payload) =>
        await client.PostAsync("/api/v1/order-intake/email", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
}
