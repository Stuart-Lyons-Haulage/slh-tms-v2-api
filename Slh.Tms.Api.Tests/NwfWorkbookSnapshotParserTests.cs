using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class NwfWorkbookSnapshotParserTests
{
    private readonly NwfWorkbookSnapshotParser parser = new();

    [Fact]
    public void WorkbookSnapshot_KeepsIncompleteFreshCutRowAsPreOrder()
    {
        var result = parser.TryParse(Request("snapshot-1"));

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(4, result.Orders.Count);

        var freshCut = Assert.Single(result.Orders.Where(order =>
            order.Payload.TryGetProperty("productPo", out var product) && product.GetString() == "PO00500277"));

        Assert.False(freshCut.Payload.GetProperty("plannerReady").GetBoolean());
        Assert.Equal("PreOrder", freshCut.Payload.GetProperty("intakeStatus").GetString());
        Assert.Equal("Merston", freshCut.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(3, freshCut.Payload.GetProperty("pallets").GetInt32());
        Assert.Contains(
            freshCut.Payload.GetProperty("intakeMatchKeys").EnumerateArray().Select(item => item.GetString()),
            key => string.Equals(key, "NWF|2026-08-18|PRODUCT:PO00500277", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(result.Orders, order =>
            order.Payload.TryGetProperty("productPo", out var product) && product.GetString() == "POOLD");
    }

    [Fact]
    public void WorkbookSnapshot_ParsesReadyInboundAndCurrentCrateReturn()
    {
        var result = parser.TryParse(Request("snapshot-ready"));

        var hobson = result!.Orders.Where(order =>
            order.Payload.TryGetProperty("productPo", out var product) && product.GetString() == "PO00499127").ToList();
        Assert.Equal(2, hobson.Count);
        Assert.All(hobson, order => Assert.True(order.Payload.GetProperty("plannerReady").GetBoolean()));

        var crate = Assert.Single(result.Orders.Where(order =>
            order.Payload.GetProperty("jobType").GetString() == "NWF crate return"));
        Assert.True(crate.Payload.GetProperty("plannerReady").GetBoolean());
        Assert.Equal("Selsey", crate.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Valefresco", crate.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(5, crate.Payload.GetProperty("pallets").GetInt32());
    }

    private static MailboxEmailIntakeRequest Request(string id) => new(
        id,
        null,
        "info@lyonshaulage.com",
        "ShiftLogisticalPlanner@nwfltd.co.uk",
        "Shift Logistical Planner",
        "NWAY SLH DAILY TRACKER - FRESH CUT ADDED FOR 18/08",
        DateTimeOffset.Parse("2026-08-17T15:25:00Z"),
        "Please see updated tracker attached.",
        null,
        null,
        [new MailboxAttachmentRequest("SLH NWF's Daily Control Tracker 2026.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", SnapshotFixture, false)]);

    private const string SnapshotFixture = "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";
}
