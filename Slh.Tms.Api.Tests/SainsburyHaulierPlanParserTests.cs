using System.IO.Compression;
using System.Text;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SainsburyHaulierPlanParserTests
{
    private readonly SainsburyHaulierPlanParser parser = new();

    [Theory]
    [InlineData("[CrosspointPCC] STUART LYONS - Crosspoint PCC Plan for delivery date 19/09/2026")]
    [InlineData("[DaventryBond] STUART LYONS - Daventry Bond Plan for delivery date 19/09/2026")]
    [InlineData("[HDKPCC] STUART LYONS - Haydock PCC Plan for delivery date 19/09/2026")]
    public void CurrentCentralTransportSubjects_AreRecognised(string subject)
    {
        var result = parser.TryParse(Request(subject, "Central.Transport@sainsburys.co.uk"));

        Assert.NotNull(result);
        Assert.Empty(result!.Orders);
        Assert.Contains("workbook", result.IgnoredReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HistoricTransportPlanSubject_RemainsRecognised()
    {
        var result = parser.TryParse(Request("Transport plan for STUART LYONS - delivery 19/09/2026", "planner@example.com"));

        Assert.NotNull(result);
        Assert.Empty(result!.Orders);
    }

    [Fact]
    public void SimilarNonSainsburySubject_IsNotClaimed()
    {
        var result = parser.TryParse(Request(
            "STUART LYONS - Customer Plan for delivery date 19/09/2026",
            "planner@anothercustomer.example"));

        Assert.Null(result);
    }

    [Fact]
    public void ScreenshotHaulierPlanSchema_StagesStuartLyonsRowAndPallets()
    {
        var request = new MailboxEmailIntakeRequest(
            "sainsbury-haydock-screenshot",
            null,
            "info@lyonshaulage.com",
            "Central.Transport@sainsburys.co.uk",
            "Central Transport",
            "[HDKPCC] STUART LYONS - Haydock PCC Plan for delivery date 27/09/2026",
            DateTimeOffset.Parse("2026-09-25T10:22:30Z"),
            "Please find attached the details for the Haydock PCC load(s).",
            null,
            null,
            [
                new MailboxAttachmentRequest(
                    "STUART LYONS.xlsx",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    Convert.ToBase64String(BuildScreenshotWorkbook()),
                    false)
            ]);

        var result = parser.TryParse(request);

        var order = Assert.Single(result!.Orders);
        Assert.Equal("SAINSBURY", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("HAYDOCK PCC", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("BASINGSTOKE", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal("2026-09-27", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal(26, order.Payload.GetProperty("pallets").GetInt32());
        Assert.DoesNotContain(order.Warnings, warning => warning.Contains("Pallet quantity", StringComparison.OrdinalIgnoreCase));
    }

    private static MailboxEmailIntakeRequest Request(string subject, string sender) => new(
        "sainsbury-subject-regression",
        null,
        "info@lyonshaulage.com",
        sender,
        "Central Transport",
        subject,
        DateTimeOffset.Parse("2026-09-17T10:16:09Z"),
        "Please find attached the details for the load(s).",
        null,
        null,
        []);

    private static byte[] BuildScreenshotWorkbook()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Add(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(archive, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(archive, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");

            var headers = new[]
            {
                "COLLECTING DEPOT / HAULIER", "COLLECTION SITE", "DESTINATION",
                "SCION ORDER NUMBER", "PALLETS", "COLLECTION DATE", "COLLECTION TIME",
                "COLLECTION DAY", "ORIGINAL DELIVERY"
            };
            var values = new[] { "STUART LYONS", "HAYDOCK PCC", "BASINGSTOKE", "302573", "26", "27/09/26", "08:00", "SUN", "06:00" };
            var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            xml.Append(Row(1, headers));
            xml.Append(Row(2, values));
            xml.Append("</sheetData></worksheet>");
            Add(archive, "xl/worksheets/sheet1.xml", xml.ToString());
        }
        return stream.ToArray();
    }

    private static string Row(int rowNumber, IReadOnlyList<string> values)
    {
        var xml = new StringBuilder($"<row r=\"{rowNumber}\">");
        for (var index = 0; index < values.Count; index++)
        {
            var column = (char)('A' + index);
            var escaped = System.Security.SecurityElement.Escape(values[index]);
            xml.Append($"<c r=\"{column}{rowNumber}\" t=\"inlineStr\"><is><t>{escaped}</t></is></c>");
        }
        return xml.Append("</row>").ToString();
    }

    private static void Add(ZipArchive archive, string name, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(contents);
    }
}
