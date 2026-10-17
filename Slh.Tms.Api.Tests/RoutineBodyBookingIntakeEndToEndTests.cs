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
/// Real routine bookings for Sunday 18 Oct 2026 that arrive in the email body. The
/// parsers for these layouts existed, but the intake gate only let Barfoots Waitrose
/// and Summer Berry bodies through, so none of these reached Order Review.
/// Each is driven through POST /api/v1/order-intake/email with the real HTML.
/// </summary>
public sealed class RoutineBodyBookingIntakeEndToEndTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public RoutineBodyBookingIntakeEndToEndTests(CustomWebFactory factory) => this.factory = factory;

    private static string Cell(string value) => $"<td style=\"border:solid #ABABAB 1.0pt\"><p style=\"text-align:center\">{value}</p></td>";

    [Fact]
    public async Task Fowler_Welch_Waitrose_table_stages_each_depot_from_Hilsea()
    {
        var html = "<div class=\"WordSection1\"><p class=\"MsoNormal\">Apologies, thought I’d sent this morning…</p><p class=\"MsoNormal\">Good morning,</p>" +
                   "<p>These are the Waitrose pallet counts for delivery tomorrow.</p><table class=\"MsoNormalTable\"><tbody>" +
                   $"<tr>{Cell("<b>DELIVERY DATE</b>")}{Cell("<b>18/10/2026</b>")}{Cell("+10°C")}</tr>" +
                   $"<tr>{Cell(" ")}{Cell(" ")}{Cell(" ")}</tr>" +
                   $"<tr>{Cell("<b>DEPOT</b>")}{Cell("<b>PO NUMBER</b>")}{Cell("<b>PALLET COUNT</b>")}</tr>" +
                   $"<tr>{Cell("AYLESFORD")}{Cell("X61046")}{Cell("3")}</tr>" +
                   $"<tr>{Cell("BRACKNELL")}{Cell("J61240")}{Cell("2")}</tr>" +
                   $"<tr>{Cell("BRINKLOW")}{Cell("R61149")}{Cell("3")}</tr>" +
                   $"<tr>{Cell("LEYLAND")}{Cell("Z59844")}{Cell("1")}</tr>" +
                   "</tbody></table><p>Kind Regards,</p><p><b>Norbert Horvath</b>  Stock Controller </p><p>Blueprint 2000, Portfield Road, Hilsea, PO3 5FJ</p></div>";

        var staged = await Intake("Tom.Whiting@fowlerwelch.co.uk", "Tom Whiting", "Waitrose 18/10/26", "2026-10-17T13:24:44Z", html);

        Assert.Equal(4, staged.Count);
        foreach (var (depot, po, pallets) in new[] { ("Aylesford", "X61046", 3), ("Bracknell", "J61240", 2), ("Brinklow", "R61149", 3), ("Leyland", "Z59844", 1) })
        {
            var row = Assert.Single(staged, item => Text(item, "stallNumber") == depot);
            Assert.Equal(po, Text(row, "customerPo"));
            Assert.Equal(pallets, row.GetProperty("pallets").GetInt32());
            Assert.Equal("2026-10-18", Text(row, "deliveryDate"));
            Assert.Equal("Fowler Welch Hilsea", Text(row, "sellerName"));
        }
    }

    [Fact]
    public async Task Hall_Hunter_direct_depot_email_stages_the_Leyland_pallets()
    {
        var html = "<p>Good morning,</p><p>Please collect 2 pallets from Hall Hunter today Saturday 17/10/2026.</p>" +
                   "<ul><li>Leyland 2 pallets</li></ul><p>For Delivery date Sunday 18/10/2026.</p>" +
                   "<p>PO number: A72358. 73 cases of Berries.</p><p>Regards,</p><p>Chris</p><p>Chris Benning</p>";

        var staged = await Intake("chris.benning@primafruit.co.uk", "Chris Benning", "HHP WAITROSE DIRECT DEPOT DELIVERY Sunday 18/10/26", "2026-10-17T08:11:12Z", html);

        var row = Assert.Single(staged);
        Assert.Equal("WAITROSE", Text(row, "customerCode"));
        Assert.Equal("Leyland", Text(row, "stallNumber"));
        Assert.Equal(2, row.GetProperty("pallets").GetInt32());
        Assert.Equal("2026-10-17", Text(row, "collectionDate"));
        Assert.Equal("2026-10-18", Text(row, "deliveryDate"));
        Assert.Equal("A72358", Text(row, "customerPo"));
    }

    [Fact]
    public async Task Emery_pallets_dropped_at_our_site_stage_one_order_per_depot()
    {
        var html = "<div>Emery Soft Fruits</div><div>Riverside Farm</div><div>Southampton</div><div>SO51 6DW</div><div> </div>" +
                   "<div>Good Morning</div><div> </div><div>For delivery Sunday 18/10/2026</div><div> </div>" +
                   "<div>3 pallets - Brinklow depot</div><div><br /></div><div>1 pallet  - Leyland depot</div><div> </div>" +
                   "<div>These will be delivered to you site today pm</div><div><br /></div>" +
                   "<div>Many Thanks</div><div class=\"signature\"><div>Maggie Thorne </div><div> 07760751701</div></div>";

        var staged = await Intake("emerypacking@aol.com", "emerypacking@aol.com", "Emery Waitrose pallets for Sunday 18/10/2026", "2026-10-17T09:27:07Z", html);

        Assert.Equal(2, staged.Count);
        var brinklow = Assert.Single(staged, item => Text(item, "stallNumber") == "Brinklow");
        Assert.Equal(3, brinklow.GetProperty("pallets").GetInt32());
        var leyland = Assert.Single(staged, item => Text(item, "stallNumber") == "Leyland");
        Assert.Equal(1, leyland.GetProperty("pallets").GetInt32());
        Assert.All(staged, row =>
        {
            Assert.Equal("WAITROSE", Text(row, "customerCode"));
            Assert.Equal("2026-10-17", Text(row, "collectionDate"));
            Assert.Equal("2026-10-18", Text(row, "deliveryDate"));
            Assert.Equal("Emery Soft Fruits (dropped at SLH)", Text(row, "sellerName"));
        });
    }

    [Fact]
    public async Task Sainsbury_GM_collection_is_staged_with_the_time_and_a_pallet_check()
    {
        var html = "<div class=\"WordSection1\"><p class=\"MsoNormal\">Hello,</p><p class=\"MsoNormal\">Please find below confirmation of your collections out of Daventry GM tomorrow <b>**</b><b>18/10/2026**</b></p>" +
                   "<table class=\"MsoNormalTable\"><tbody>" +
                   $"<tr>{Cell("<b>Collection Date</b>")}{Cell("<b>Collecting Depot / Haulier</b>")}{Cell("<b>Collection Site</b>")}{Cell("<b>Destination</b>")}{Cell("<b>Collection Time</b>")}</tr>" +
                   $"<tr>{Cell("<b>18/10/2026</b>")}{Cell("Stuart Lyons")}{Cell("Daventry GM")}{Cell("BASINGSTOKE")}{Cell("10:00")}</tr>" +
                   "</tbody></table><div><p class=\"MsoNormal\">Kind Regards,</p><p class=\"MsoNormal\">Daniel McIlree | GTP | 02476 439561</p></div></div>" +
                   "<div><hr /><p>Sainsbury's Supermarkets Ltd (3261722 England)<br />Registered Offices: 33 Charterhouse Street, London, EC1M 6HA</p></div>";

        var messageId = $"e2e-gm-{Guid.NewGuid():N}";
        var staged = await Intake("Daniel.Mcilree@sainsburys.co.uk", "Daniel Mcilree", "Stuart Lyons GM Collections 18/10/2026", "2026-10-17T09:25:03Z", html, messageId);

        var row = Assert.Single(staged);
        Assert.Equal("SAINSBURY", Text(row, "customerCode"));
        Assert.Equal("Daventry GM", Text(row, "sellerName"));
        Assert.Equal("Basingstoke", Text(row, "stallNumber"));
        Assert.Equal("2026-10-18", Text(row, "collectionDate"));
        Assert.Equal("10:00", Text(row, "requestedTime"));
        Assert.Contains(row.GetProperty("intakeWarnings").EnumerateArray(), warning => warning.GetString()!.Contains("no pallet count", StringComparison.Ordinal));
    }

    [Fact]
    public void Supermarkets_in_a_footer_is_not_a_market_order()
    {
        var request = new MailboxEmailIntakeRequest(
            "m-supermarkets", null, "info@lyonshaulage.com", "Backhaul.Requests@sainsburys.co.uk", "Backhaul",
            "Transport plan for STUART LYONS for 18/10/2026 - Version 1", DateTimeOffset.Parse("2026-10-17T13:09:20Z"),
            "Dear STUART LYONS, Please find transport plan attached.",
            "<p>Dear STUART LYONS,</p><p>Sainsbury's Supermarkets Ltd (3261722 England)</p>", null, null);
        Assert.True(Slh.Tms.Api.Controllers.OrderIntakeController.IsApprovedAutomaticSource(request));

        var market = request with { Subject = "Wholesale Market Pallet Bookings for delivery on 19/10/26", SenderAddress = "Gosia.Mydlak@barfoots.co.uk" };
        Assert.False(Slh.Tms.Api.Controllers.OrderIntakeController.IsApprovedAutomaticSource(market));
    }

    [Fact]
    public async Task Fowler_Welch_request_for_delivery_times_is_not_an_order()
    {
        var staged = await Intake("Rita.Butcher@fowlerwelch.co.uk", "Rita Butcher", "Waitrose Delivery Times", "2026-10-17T12:00:14Z",
            "<p>Good Afternoon</p><p>Please could you send over the Waitrose delivery times for today deliveries ASAP.</p><p>Thank you</p>");

        Assert.Empty(staged);
    }

    private async Task<List<JsonElement>> Intake(string sender, string senderName, string subject, string receivedAtUtc, string html, string? messageId = null)
    {
        messageId ??= $"e2e-routine-{Guid.NewGuid():N}";
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(JsonSerializer.Serialize(new
        {
            messageId,
            internetMessageId = $"<{messageId}@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = sender,
            senderName,
            subject,
            receivedAtUtc = DateTimeOffset.Parse(receivedAtUtc),
            bodyHtml = html,
            bodyFormat = "html",
            webLink = "https://outlook.office.com/mail/e2e"
        }), Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, $"Intake returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var rows = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview && item.PayloadJson.Contains(messageId))
            .Select(item => item.PayloadJson)
            .ToListAsync();
        var staged = rows.Select(json => JsonDocument.Parse(json).RootElement.Clone()).ToList();
        // Mapping-exception placeholders are not orders.
        return staged.Where(item => Text(item, "intakeStatus") != "MappingException").ToList();
    }

    private static string? Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
