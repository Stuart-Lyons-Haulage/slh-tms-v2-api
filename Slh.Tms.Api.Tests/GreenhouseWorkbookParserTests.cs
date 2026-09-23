using System.IO.Compression;
using System.Text;
using Slh.Tms.Api.Services;
using Xunit;
using IntakeParser = Slh.Tms.Api.Controllers.SpecialistMailboxOrderParser;

namespace Slh.Tms.Api.Tests;

public sealed class GreenhouseWorkbookParserTests
{
    [Fact]
    public void FridayAldiWorkbook_StagesOnlyPositiveGreenhouseRows()
    {
        var request = new MailboxEmailIntakeRequest(
            "ghs-friday-aldi-180926",
            null,
            "info@lyonshaulage.com",
            "planner@greenhouse.example",
            "Greenhouse Planner",
            "Friday Aldi",
            DateTimeOffset.Parse("2026-09-17T12:03:18Z"),
            "Please see attached.",
            null,
            null,
            [new MailboxAttachmentRequest(
                "GHS Aldi Bookings 180926.xlsm",
                "application/vnd.ms-excel.sheet.macroenabled.12",
                Convert.ToBase64String(BuildWorkbook()),
                false)]);

        var result = new IntakeParser().TryParse(request);

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(2, result.Orders.Count);
        Assert.Equal(6, result.Orders.Sum(order => order.Payload.GetProperty("pallets").GetInt32()));
        Assert.All(result.Orders, order =>
        {
            Assert.Equal("GHS", order.Payload.GetProperty("customerCode").GetString());
            Assert.Equal("Greenhouse", order.Payload.GetProperty("sellerName").GetString());
            Assert.Equal("ALDI", order.Payload.GetProperty("retailerCode").GetString());
            Assert.Equal("Euro", order.Payload.GetProperty("palletType").GetString());
            Assert.Equal("+3°C", order.Payload.GetProperty("temperatureRequirement").GetString());
            Assert.Equal("2026-09-18", order.Payload.GetProperty("collectionDate").GetString());
        });
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "ALDI-ATHERSTONE");
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "ALDI-SWINDON");
    }

    private static byte[] BuildWorkbook()
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
            Add(archive, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Bookings" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Add(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            Add(archive, "xl/worksheets/sheet1.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="1">
                      <c r="A1" t="inlineStr"><is><t>Date</t></is></c>
                      <c r="B1" t="inlineStr"><is><t>Collection Site</t></is></c>
                      <c r="C1" t="inlineStr"><is><t>Depot Description</t></is></c>
                      <c r="D1" t="inlineStr"><is><t>Pallets</t></is></c>
                      <c r="E1" t="inlineStr"><is><t>Temperature</t></is></c>
                      <c r="F1" t="inlineStr"><is><t>Pallet Type</t></is></c>
                    </row>
                    <row r="2">
                      <c r="A2"><v>46283</v></c><c r="B2" t="inlineStr"><is><t>Greenhouse</t></is></c>
                      <c r="C2" t="inlineStr"><is><t>ALDI-BOLTON</t></is></c><c r="E2" t="inlineStr"><is><t>+3°C</t></is></c><c r="F2" t="inlineStr"><is><t>Euro</t></is></c>
                    </row>
                    <row r="3">
                      <c r="A3"><v>46283</v></c><c r="B3" t="inlineStr"><is><t>Greenhouse</t></is></c>
                      <c r="C3" t="inlineStr"><is><t>ALDI-ATHERSTONE</t></is></c><c r="D3"><v>3</v></c><c r="E3" t="inlineStr"><is><t>+3°C</t></is></c><c r="F3" t="inlineStr"><is><t>Euro</t></is></c>
                    </row>
                    <row r="4">
                      <c r="A4"><v>46283</v></c><c r="B4" t="inlineStr"><is><t>Greenhouse</t></is></c>
                      <c r="C4" t="inlineStr"><is><t>ALDI-SWINDON</t></is></c><c r="D4"><v>3</v></c><c r="E4" t="inlineStr"><is><t>+3°C</t></is></c><c r="F4" t="inlineStr"><is><t>Euro</t></is></c>
                    </row>
                  </sheetData>
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
