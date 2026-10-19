using System.IO.Compression;
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
/// Real case, 17 Oct 2026: Phil Sampson (The Greenhouse) sent "Sunday Aldi" with
/// "GHS Aldi Bookings 181026.xlsm" (Atherstone 3, Swindon 2, Cardiff 3), then 23
/// minutes later "CHANGE TO SUNDAY ALDI" - "Now 3 Swindon not 2" - with the whole
/// workbook again (Swindon 3). The resend is the complete booking for that day.
/// Each test uses its own delivery date so their bookings do not interact.
/// </summary>
public sealed class GreenhouseBookingResendEndToEndTests : IClassFixture<CustomWebFactory>
{
    private const string Phil = "phil.sampson@thegreenhousesussex.co.uk";
    private const string Pia = "Pia.Ellis@thegreenhousesussex.co.uk";
    private readonly CustomWebFactory factory;

    public GreenhouseBookingResendEndToEndTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Resent_booking_from_a_colleague_replaces_every_pending_row()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        const int serial = 46313; // 18/10/2026
        var original = $"ghs-original-{Guid.NewGuid():N}";
        var change = $"ghs-change-{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.Accepted, (await Post(client, original, Phil, "Sunday Aldi ", "2026-10-17T12:20:14Z", serial, ("ALDI-ATHERSTONE", 3), ("ALDI-SWINDON", 2), ("ALDI-CARDIFF", 3))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(client, change, Pia, "CHANGE TO SUNDAY ALDI ", "2026-10-17T12:43:19Z", serial, ("ALDI-ATHERSTONE", 3), ("ALDI-SWINDON", 3), ("ALDI-CARDIFF", 3))).StatusCode);

        var pending = await PendingFor("2026-10-18");
        Assert.Equal(3, pending.Count);
        Assert.All(pending, row => Assert.Contains(change, row.Json));
        Assert.Equal(3, Pallets(Assert.Single(pending, row => row.Json.Contains("ALDI-SWINDON", StringComparison.OrdinalIgnoreCase))));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Equal(3, await db.StagedImports.CountAsync(item => item.EntityType == "order" && item.Status == StagingStatus.Archived && item.PayloadJson.Contains(original)));
    }

    [Fact]
    public async Task Depot_missing_from_the_resent_booking_is_flagged_not_left_silently()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        const int serial = 46314; // 19/10/2026
        var original = $"ghs-drop-original-{Guid.NewGuid():N}";
        var resend = $"ghs-drop-resend-{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.Accepted, (await Post(client, original, Phil, "Monday Aldi", "2026-10-17T12:20:14Z", serial, ("ALDI-ATHERSTONE", 3), ("ALDI-SWINDON", 2), ("ALDI-CARDIFF", 3))).StatusCode);
        var response = await Post(client, resend, Phil, "CHANGE TO MONDAY ALDI", "2026-10-17T12:43:19Z", serial, ("ALDI-ATHERSTONE", 3), ("ALDI-SWINDON", 2), ("ALDI-CARDIFF", 0));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            Assert.Equal(1, body.RootElement.GetProperty("removedFromBooking").GetInt32());

        var pending = await PendingFor("2026-10-19");
        Assert.Equal(3, pending.Count);
        var cardiff = Assert.Single(pending, row => row.Json.Contains("ALDI-CARDIFF", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(original, cardiff.Json);
        using var document = JsonDocument.Parse(cardiff.Json);
        Assert.Contains(document.RootElement.GetProperty("intakeWarnings").EnumerateArray(),
            warning => warning.GetString()!.StartsWith("Not in the newer booking from", StringComparison.Ordinal));
        Assert.True(document.RootElement.TryGetProperty("missingFromNewerBooking", out _));
    }

    [Fact]
    public async Task Change_after_approval_is_one_amendment_that_updates_the_live_order()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        const int serial = 46315; // 20/10/2026
        var original = $"ghs-live-original-{Guid.NewGuid():N}";
        var change = $"ghs-live-change-{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.Accepted, (await Post(client, original, Phil, "Tuesday Aldi", "2026-10-17T12:20:14Z", serial, ("ALDI-ATHERSTONE", 3), ("ALDI-SWINDON", 2), ("ALDI-CARDIFF", 3))).StatusCode);
        foreach (var row in await PendingFor("2026-10-20"))
            Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/staging/{row.Id}/approve", new { note = "Original approved" })).StatusCode);

        Guid swindonOrderId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var live = await db.TransportOrders.Where(order => order.DeliveryDate == new DateOnly(2026, 10, 20) && order.CustomerCode == "GHS").ToListAsync();
            Assert.Equal(3, live.Count);
            swindonOrderId = Assert.Single(live, order => order.Reference.ToUpper().Contains("SWINDON")).Id;
        }

        Assert.Equal(HttpStatusCode.Accepted, (await Post(client, change, Phil, "CHANGE TO TUESDAY ALDI", "2026-10-17T12:43:19Z", serial, ("ALDI-ATHERSTONE", 3), ("ALDI-SWINDON", 3), ("ALDI-CARDIFF", 3))).StatusCode);

        // Unchanged depots are exact duplicates of live orders; only Swindon needs review.
        var pending = await PendingFor("2026-10-20");
        var swindon = Assert.Single(pending);
        Assert.Contains("ALDI-SWINDON", swindon.Json, StringComparison.OrdinalIgnoreCase);

        var comparison = await client.GetAsync($"/api/v1/order-intake/duplicate-check/staging/{swindon.Id}/comparison");
        Assert.Equal(HttpStatusCode.OK, comparison.StatusCode);
        using (var document = JsonDocument.Parse(await comparison.Content.ReadAsStringAsync()))
            Assert.Equal("Amendment/update", document.RootElement.GetProperty("classification").GetString());

        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, $"/api/v1/staging/{swindon.Id}/approve", new { note = "Swindon now 3" })).StatusCode);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var swindonOrders = await verifyDb.TransportOrders.Where(order => order.DeliveryDate == new DateOnly(2026, 10, 20) && order.Reference.ToUpper().Contains("SWINDON")).ToListAsync();
        var updated = Assert.Single(swindonOrders);
        Assert.Equal(swindonOrderId, updated.Id);
        Assert.Equal(3, updated.Pallets);
    }

    private async Task<List<(Guid Id, string Json)>> PendingFor(string deliveryDate)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var marker = $"\"deliveryDate\":\"{deliveryDate}\"";
        var rows = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview &&
                           item.PayloadJson.Contains("thegreenhousesussex") && item.PayloadJson.Contains(marker))
            .Select(item => new { item.Id, item.PayloadJson })
            .ToListAsync();
        return rows.Select(row => (row.Id, row.PayloadJson)).ToList();
    }

    private static int Pallets((Guid Id, string Json) row)
    {
        using var document = JsonDocument.Parse(row.Json);
        return document.RootElement.GetProperty("pallets").GetInt32();
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string messageId, string sender, string subject, string receivedAtUtc, int serial, params (string Depot, int Pallets)[] rows) =>
        PostJson(client, "/api/v1/order-intake/email", new
        {
            messageId,
            internetMessageId = $"<{messageId}@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = sender,
            senderName = "The Greenhouse",
            subject,
            receivedAtUtc = DateTimeOffset.Parse(receivedAtUtc),
            bodyText = subject.StartsWith("CHANGE", StringComparison.Ordinal) ? "Now updated" : string.Empty,
            webLink = "https://outlook.office.com/mail/e2e",
            attachments = new[]
            {
                new
                {
                    name = "GHS Aldi Bookings 181026.xlsm",
                    contentType = "application/vnd.ms-excel.sheet.macroenabled.12",
                    contentBase64 = Convert.ToBase64String(BuildGreenhouseWorkbook(serial, rows)),
                    isInline = false
                }
            }
        });

    private static async Task<HttpResponseMessage> PostJson(HttpClient client, string url, object payload) =>
        await client.PostAsync(url, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

    // Mirrors the real "Bookings" sheet: a title row with the depot date serial, the
    // header row, then one row per Aldi depot (blank pallets = nothing booked).
    private static byte[] BuildGreenhouseWorkbook(int serial, (string Depot, int Pallets)[] rows)
    {
        var sheet = new StringBuilder();
        sheet.Append($"""<row r="1"><c r="C1" t="inlineStr"><is><t>ALDI orders depot date:</t></is></c><c r="D1"><v>{serial}</v></c></row>""");
        sheet.Append("""
            <row r="2">
              <c r="A2" t="inlineStr"><is><t>Date</t></is></c><c r="B2" t="inlineStr"><is><t>Collection Site</t></is></c>
              <c r="C2" t="inlineStr"><is><t>Depot Description</t></is></c><c r="D2" t="inlineStr"><is><t>Pallets</t></is></c>
              <c r="E2" t="inlineStr"><is><t>Temperature</t></is></c><c r="F2" t="inlineStr"><is><t>Pallet Type</t></is></c>
            </row>
            """);
        var allDepots = new[] { "ALDI-BOLTON", "ALDI-NESTON", "ALDI-ATHERSTONE", "ALDI-SWINDON", "ALDI-CARDIFF", "ALDI-CHELMSFORD" };
        var rowNumber = 3;
        foreach (var depot in allDepots)
        {
            var pallets = rows.FirstOrDefault(row => row.Depot == depot).Pallets;
            var palletCell = pallets > 0 ? $"""<c r="D{rowNumber}"><v>{pallets}</v></c>""" : string.Empty;
            sheet.Append($"""
                <row r="{rowNumber}"><c r="A{rowNumber}"><v>{serial}</v></c><c r="B{rowNumber}" t="inlineStr"><is><t>Greenhouse</t></is></c>
                <c r="C{rowNumber}" t="inlineStr"><is><t>{depot}</t></is></c>{palletCell}
                <c r="E{rowNumber}" t="inlineStr"><is><t>+3°C</t></is></c><c r="F{rowNumber}" t="inlineStr"><is><t>Euro</t></is></c></row>
                """);
            rowNumber++;
        }
        return BuildXlsx("Bookings", sheet.ToString());
    }

    private static byte[] BuildXlsx(string sheetName, string sheetRows)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            Add(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Add(archive, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="{sheetName}" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Add(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            Add(archive, "xl/worksheets/sheet1.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>{sheetRows}</sheetData>
                </worksheet>
                """);
        }
        return output.ToArray();
    }

    private static void Add(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
