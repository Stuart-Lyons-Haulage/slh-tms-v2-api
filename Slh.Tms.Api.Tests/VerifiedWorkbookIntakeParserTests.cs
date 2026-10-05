using System.IO.Compression;
using System.Text;
using Slh.Tms.Api.Services;
using Xunit;
using IntakeParser = Slh.Tms.Api.Controllers.SpecialistMailboxOrderParser;

namespace Slh.Tms.Api.Tests;

public sealed class VerifiedWorkbookIntakeParserTests
{
    [Fact]
    public void BarfootsAldiConfirmedWorkbook_ImportsOnlyPositiveRows_WithSourceEvidence()
    {
        var request = BarfootsRequest("barfoots-aldi-1", BuildBarfootsWorkbook(13));

        var result = new IntakeParser().TryParse(request);

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(2, result.Orders.Count);
        Assert.Equal(18, result.Orders.Sum(order => order.Payload.GetProperty("pallets").GetInt32()));
        Assert.All(result.Orders, order =>
        {
            Assert.Equal("BARFOOTS", order.Payload.GetProperty("customerCode").GetString());
            Assert.Equal("ALDI", order.Payload.GetProperty("retailerCode").GetString());
            Assert.Equal("Thursday 24.09.2026.xlsx", order.Payload.GetProperty("sourceAttachmentName").GetString());
            Assert.Equal("BARFOOTS_ALDI_CONFIRMED_WORKBOOK", order.Payload.GetProperty("intakeProfile").GetString());
            Assert.False(string.IsNullOrWhiteSpace(order.Payload.GetProperty("intakeNaturalKey").GetString()));
        });
        Assert.Contains(result.Orders, order =>
            order.Payload.GetProperty("sellerName").GetString() == "Sefter North" &&
            order.Payload.GetProperty("collectionSite").GetString() == "Sefter North" &&
            order.Payload.GetProperty("temperatureRequirement").GetString() == "+10℃");
        Assert.Contains(result.Orders, order =>
            order.Payload.GetProperty("sellerName").GetString() == "Sefter South" &&
            order.Payload.GetProperty("collectionPoint").GetString() == "Sefter South" &&
            order.Payload.GetProperty("temperatureRequirement").GetString() == "+3℃");
    }

    [Fact]
    public void BarfootsAldiAmendment_PalletQuantityChange_DoesNotChangeLogicalIdentity()
    {
        var first = new IntakeParser().TryParse(BarfootsRequest("barfoots-aldi-original", BuildBarfootsWorkbook(13)));
        var amended = new IntakeParser().TryParse(BarfootsRequest("barfoots-aldi-amended", BuildBarfootsWorkbook(14)));

        Assert.NotNull(first);
        Assert.NotNull(amended);

        var firstNorth = Assert.Single(first!.Orders.Where(order =>
            order.Payload.GetProperty("sellerName").GetString() == "Sefter North"));
        var amendedNorth = Assert.Single(amended!.Orders.Where(order =>
            order.Payload.GetProperty("sellerName").GetString() == "Sefter North"));

        Assert.Equal(firstNorth.NaturalKey, amendedNorth.NaturalKey);
        Assert.Equal(
            firstNorth.Payload.GetProperty("intakeNaturalKey").GetString(),
            amendedNorth.Payload.GetProperty("intakeNaturalKey").GetString());
        Assert.Equal(13, firstNorth.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal(14, amendedNorth.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void NwfConfirmedAldiCollectionWorkbook_UsesPlanningDate_AndSplitsCompoundSite()
    {
        var workbook = BuildNwfConfirmedAldiCollectionWorkbook();
        var attachment = new MailboxAttachmentRequest(
            "SLH Collection Sheet 05.10.26.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Convert.ToBase64String(workbook),
            false);
        var request = new MailboxEmailIntakeRequest(
            "nwf-aldi-confirmed-0710",
            null,
            "info@lyonshaulage.com",
            "MariuszUrbanski@nwfltd.co.uk",
            "Mariusz Urbanski",
            "Aldi Bedford - confirmation for 07/10",
            DateTimeOffset.Parse("2026-10-05T13:51:19Z"),
            """
            Good afternoon,
            Please see confirmed ALDI trays collection for 07/10
            ALDI
            PO00507451
            £330.35
            07/10/2026
            08/10/2026
            28
            Bedford
            228703159
            PO00506489
            Selsey
            33
            """,
            null,
            null,
            [attachment, attachment]);

        var result = new IntakeParser().TryParse(request);

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(3, result.Orders.Count);
        Assert.Equal(66, result.Orders.Sum(order => order.Payload.GetProperty("pallets").GetInt32()));
        Assert.Contains(result.Orders, order =>
            order.Payload.GetProperty("sellerName").GetString() == "Selsey" &&
            order.Payload.GetProperty("pallets").GetInt32() == 33);
        Assert.Contains(result.Orders, order =>
            order.Payload.GetProperty("sellerName").GetString() == "Merston" &&
            order.Payload.GetProperty("pallets").GetInt32() == 13);
        Assert.Contains(result.Orders, order =>
            order.Payload.GetProperty("sellerName").GetString() == "Runcton" &&
            order.Payload.GetProperty("pallets").GetInt32() == 20);
        Assert.All(result.Orders, order =>
        {
            Assert.Equal("ALDI", order.Payload.GetProperty("customerCode").GetString());
            Assert.Equal("Bedford", order.Payload.GetProperty("stallNumber").GetString());
            Assert.Equal("2026-10-07", order.Payload.GetProperty("collectionDate").GetString());
            Assert.Equal("2026-10-08", order.Payload.GetProperty("deliveryDate").GetString());
            Assert.Equal("Collections", order.Payload.GetProperty("sourceSheet").GetString());
            Assert.Equal("NWF_CONFIRMED_ALDI_COLLECTION_WORKBOOK", order.Payload.GetProperty("intakeProfile").GetString());
            Assert.Equal("NWF confirmed ALDI tray collection", order.Payload.GetProperty("jobType").GetString());
        });
        Assert.Single(result.Orders.Where(order => order.Payload.GetProperty("transportPo").GetString() == "PO00507451"));
        Assert.Equal(2, result.Orders.Count(order => order.Payload.GetProperty("transportPo").GetString() == "PO00507452"));
    }

    [Fact]
    public void NwfConfirmedAldiCollectionWorkbook_PalletAmendmentKeepsLogicalIdentity()
    {
        static MailboxEmailIntakeRequest Request(byte[] workbook, string messageId) => new(
            messageId,
            null,
            "info@lyonshaulage.com",
            "MariuszUrbanski@nwfltd.co.uk",
            "Mariusz Urbanski",
            "Aldi Bedford - confirmation for 07/10",
            DateTimeOffset.Parse("2026-10-05T13:51:19Z"),
            "Please see confirmed ALDI trays collection for 07/10",
            null,
            null,
            [new MailboxAttachmentRequest(
                "SLH Collection Sheet 05.10.26.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Convert.ToBase64String(workbook),
                false)]);

        var original = new IntakeParser().TryParse(Request(BuildNwfConfirmedAldiCollectionWorkbook(33), "original"));
        var amended = new IntakeParser().TryParse(Request(BuildNwfConfirmedAldiCollectionWorkbook(34), "amended"));

        Assert.NotNull(original);
        Assert.NotNull(amended);
        var originalSelsey = Assert.Single(original!.Orders.Where(order => order.Payload.GetProperty("sellerName").GetString() == "Selsey"));
        var amendedSelsey = Assert.Single(amended!.Orders.Where(order => order.Payload.GetProperty("sellerName").GetString() == "Selsey"));
        Assert.Equal(originalSelsey.NaturalKey, amendedSelsey.NaturalKey);
        Assert.Equal(33, originalSelsey.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal(34, amendedSelsey.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void NwfConfirmedAldiCollectionWorkbook_CancellationDoesNotCreateNewOrders()
    {
        var request = new MailboxEmailIntakeRequest(
            "cancelled-nwf-aldi-confirmed-0710",
            null,
            "info@lyonshaulage.com",
            "MariuszUrbanski@nwfltd.co.uk",
            "Mariusz Urbanski",
            "Cancellation - Aldi Bedford confirmation for 07/10",
            DateTimeOffset.Parse("2026-10-05T13:51:19Z"),
            "The confirmed ALDI trays collection for 07/10 is cancelled.",
            null,
            null,
            [new MailboxAttachmentRequest(
                "SLH Collection Sheet 05.10.26.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Convert.ToBase64String(BuildNwfConfirmedAldiCollectionWorkbook()),
                false)]);

        var result = new IntakeParser().TryParse(request);

        Assert.NotNull(result);
        Assert.Empty(result!.Orders);
        Assert.Contains("Cancellation", result.IgnoredReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WealmoorWaitroseEstimate_AggregatesTemperatureBands_PerDepot()
    {
        var request = new MailboxEmailIntakeRequest(
            "wealmoor-waitrose-1",
            null,
            "info@lyonshaulage.com",
            "Dispatch.Greenford@wealmoor.co.uk",
            "Greenford Dispatch",
            "WAITROSE PALLET ESTIMATE for 24.09.2026",
            DateTimeOffset.Parse("2026-09-23T09:20:54Z"),
            "Please find attached Waitrose pallet estimates for 24.09.2026",
            null,
            null,
            [new MailboxAttachmentRequest(
                "WAITROSE PALLET ESTIMATE 24.09.2026.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Convert.ToBase64String(BuildWealmoorWorkbook()),
                false)]);

        var result = new IntakeParser().TryParse(request);

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(2, result.Orders.Count);

        var aylesford = Assert.Single(result.Orders.Where(order =>
            order.Payload.GetProperty("stallNumber").GetString() == "Aylesford"));
        Assert.Equal("WEALMOOR", aylesford.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("WAITROSE", aylesford.Payload.GetProperty("retailerCode").GetString());
        Assert.Equal(10, aylesford.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("2026-09-23", aylesford.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-09-24", aylesford.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal("+12°C / +2°C", aylesford.Payload.GetProperty("temperatureRequirement").GetString());
        Assert.Equal(8, aylesford.Payload.GetProperty("palletBreakdown").GetProperty("frvPlus12").GetInt32());
        Assert.Equal(2, aylesford.Payload.GetProperty("palletBreakdown").GetProperty("chilledPlus2").GetInt32());
        Assert.Equal("WEALMOOR_WAITROSE_PALLET_ESTIMATE", aylesford.Payload.GetProperty("intakeProfile").GetString());
        Assert.Equal("WAITROSE PALLET ESTIMATE 24.09.2026.xlsx", aylesford.Payload.GetProperty("sourceAttachmentName").GetString());

        var bracknell = Assert.Single(result.Orders.Where(order =>
            order.Payload.GetProperty("stallNumber").GetString() == "Bracknell"));
        Assert.Equal(11, bracknell.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void WealmoorWorkbook_FromUnverifiedSender_IsNotAutomaticOrderIntake()
    {
        var request = new MailboxEmailIntakeRequest(
            "wealmoor-spoof",
            null,
            "info@lyonshaulage.com",
            "dispatch@example.com",
            "Unknown Dispatch",
            "WAITROSE PALLET ESTIMATE for 24.09.2026",
            DateTimeOffset.Parse("2026-09-23T09:20:54Z"),
            "Please find attached Waitrose pallet estimates for 24.09.2026",
            null,
            null,
            [new MailboxAttachmentRequest(
                "WAITROSE PALLET ESTIMATE 24.09.2026.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Convert.ToBase64String(BuildWealmoorWorkbook()),
                false)]);

        var result = new IntakeParser().TryParse(request);

        Assert.Null(result);
    }

    private static MailboxEmailIntakeRequest BarfootsRequest(string messageId, byte[] workbook) =>
        new(
            messageId,
            null,
            "info@lyonshaulage.com",
            "Agnieszka.Zawislan@barfoots.co.uk",
            "Agnieszka Zawislan",
            "AMENDED Aldi Confirmed Booking for delivery Thursday 24.09.2026",
            DateTimeOffset.Parse("2026-09-23T13:35:05Z"),
            "Please see attached Aldi confirmed pallet booking for delivery tomorrow.",
            null,
            null,
            [new MailboxAttachmentRequest(
                "Thursday 24.09.2026.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Convert.ToBase64String(workbook),
                false)]);

    private static byte[] BuildBarfootsWorkbook(int northPallets)
    {
        var rows = $"""
            <row r="1"><c r="C1" t="inlineStr"><is><t>ALDI orders depot date:</t></is></c><c r="D1"><v>46289</v></c></row>
            <row r="3">
              <c r="B3" t="inlineStr"><is><t>Depot Description</t></is></c>
              <c r="C3" t="inlineStr"><is><t>Depot Description</t></is></c>
              <c r="D3" t="inlineStr"><is><t>Collection Site</t></is></c>
              <c r="E3" t="inlineStr"><is><t>Temp.</t></is></c>
              <c r="F3" t="inlineStr"><is><t>Pallets</t></is></c>
            </row>
            <row r="4">
              <c r="A4"><v>46289</v></c><c r="B4" t="inlineStr"><is><t>ALDI CHELMSFORD</t></is></c>
              <c r="D4" t="inlineStr"><is><t>Sefter North</t></is></c>
              <c r="E4" t="inlineStr"><is><t>+10℃</t></is></c><c r="F4"><v>{northPallets}</v></c>
            </row>
            <row r="5">
              <c r="A5"><v>46289</v></c><c r="B5" t="inlineStr"><is><t>ALDI CHELMSFORD</t></is></c>
              <c r="D5" t="inlineStr"><is><t>Sefter South</t></is></c>
              <c r="E5" t="inlineStr"><is><t>+3℃</t></is></c><c r="F5"><v>5</v></c>
            </row>
            <row r="6">
              <c r="A6"><v>46289</v></c><c r="B6" t="inlineStr"><is><t>ALDI SHEPPEY</t></is></c>
              <c r="D6" t="inlineStr"><is><t>Leythorne</t></is></c>
              <c r="E6" t="inlineStr"><is><t>+3℃</t></is></c><c r="F6"><v>0</v></c>
            </row>
            """;
        return BuildXlsx("Aldi", rows);
    }

    private static byte[] BuildNwfConfirmedAldiCollectionWorkbook(int selseyPallets = 33)
    {
        var rows = $$"""
            <row r="3">
              <c r="A3" t="inlineStr"><is><t>CUSTOMER</t></is></c>
              <c r="B3" t="inlineStr"><is><t>Transport PO </t></is></c>
              <c r="D3" t="inlineStr"><is><t>COLLECTION DATE</t></is></c>
              <c r="E3" t="inlineStr"><is><t>DELIVERY DATE</t></is></c>
              <c r="G3" t="inlineStr"><is><t>COLLECTION DEPOT </t></is></c>
              <c r="H3" t="inlineStr"><is><t>COLLECTION REFERENCE </t></is></c>
              <c r="I3" t="inlineStr"><is><t>NWF CRATE PO NUMBER </t></is></c>
              <c r="J3" t="inlineStr"><is><t>DELIVERY SITE</t></is></c>
              <c r="K3" t="inlineStr"><is><t>Pallets</t></is></c>
              <c r="L3" t="inlineStr"><is><t>Comments</t></is></c>
            </row>
            <row r="4">
              <c r="A4" t="inlineStr"><is><t>ALDI</t></is></c>
              <c r="B4" t="inlineStr"><is><t>PO00507451</t></is></c>
              <c r="D4"><v>46302</v></c><c r="E4"><v>46303</v></c>
              <c r="G4" t="inlineStr"><is><t>Bedford</t></is></c>
              <c r="H4"><v>228703159</v></c>
              <c r="I4" t="inlineStr"><is><t>PO00506489</t></is></c>
              <c r="J4" t="inlineStr"><is><t>Selsey</t></is></c><c r="K4"><v>{{selseyPallets}}</v></c>
            </row>
            <row r="5">
              <c r="A5" t="inlineStr"><is><t>ALDI</t></is></c>
              <c r="B5" t="inlineStr"><is><t>PO00507452</t></is></c>
              <c r="D5"><v>46302</v></c><c r="E5"><v>46303</v></c>
              <c r="G5" t="inlineStr"><is><t>Bedford</t></is></c>
              <c r="H5"><v>228703167</v></c>
              <c r="I5" t="inlineStr"><is><t>PO00506495</t></is></c>
              <c r="J5" t="inlineStr"><is><t>Runcton/Merston</t></is></c><c r="K5"><v>33</v></c>
              <c r="L5" t="inlineStr"><is><t>13p Merston &amp; 20p Runcton</t></is></c>
            </row>
            <row r="6">
              <c r="A6" t="inlineStr"><is><t>ALDI</t></is></c>
              <c r="B6" t="inlineStr"><is><t>PO00509999</t></is></c>
              <c r="D6"><v>46303</v></c><c r="E6"><v>46304</v></c>
              <c r="G6" t="inlineStr"><is><t>Bedford</t></is></c>
              <c r="I6" t="inlineStr"><is><t>PO00509998</t></is></c>
              <c r="J6" t="inlineStr"><is><t>Selsey</t></is></c><c r="K6"><v>26</v></c>
            </row>
            """;
        return BuildXlsx("Collections", rows);
    }

    private static byte[] BuildWealmoorWorkbook()
    {
        const string rows = """
            <row r="1"><c r="A1" t="inlineStr"><is><t>DATE</t></is></c><c r="B1"><v>46289</v></c></row>
            <row r="3">
              <c r="A3" t="inlineStr"><is><t>Depot</t></is></c>
              <c r="B3" t="inlineStr"><is><t> FRV (+12)</t></is></c>
              <c r="C3" t="inlineStr"><is><t>Cases</t></is></c>
              <c r="D3" t="inlineStr"><is><t> CHL (+2)</t></is></c>
              <c r="E3" t="inlineStr"><is><t>Cases</t></is></c>
            </row>
            <row r="4">
              <c r="A4" t="inlineStr"><is><t>Aylesford</t></is></c><c r="B4"><v>8</v></c><c r="C4"><v>760</v></c><c r="D4"><v>2</v></c><c r="E4"><v>238</v></c>
            </row>
            <row r="5">
              <c r="A5" t="inlineStr"><is><t>Bracknell</t></is></c><c r="B5"><v>9</v></c><c r="C5"><v>859</v></c><c r="D5"><v>2</v></c><c r="E5"><v>305</v></c>
            </row>
            """;
        return BuildXlsx("Pallets", rows);
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
