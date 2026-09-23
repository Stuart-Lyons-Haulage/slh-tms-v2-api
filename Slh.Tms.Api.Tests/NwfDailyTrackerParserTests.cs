using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class NwfDailyTrackerParserTests
{
    private readonly NwfDailyTrackerParser parser = new();

    [Fact]
    public void AmendmentRows_CreateOneOrderPerPositiveDepotSplit()
    {
        var body = """
19/08/2026| **PO00500645**| SLH1908B| PO00499321| Blackdown and Valefresco| | | | 5| 26| 5| | | Just Crates from Selsey to Valfresco
---|---|---|---|---|---|---|---|---|---|---|---|---|---
19/08/2026| **PO00500646**| SLH1908C| PO00499127| Hobson Farming| 15| 7| | | 22| 22| | |
19/08/2026| **PO00500647**| SLH1908D| PO00500519| Stourgardens| 2| 4| | | 6| 6| | |
19/08/2026| ** **| SLH| PO00499608| Natural Innovations Limited| 1| | | | 1| 1| | |
""";
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "nwf-1", null, "info@lyonshaulage.com", "ShiftLogisticalPlanner@nwfltd.co.uk", "Shift Logistical Planner",
            "NWAY SLH DAILY TRACKER AMENDED WITH CRATES RETURN FOR VALFRESCO + BLACKDOWN GROWERS COLLECTION ON 19TH",
            DateTimeOffset.Parse("2026-08-17T13:48:47Z"), body, null, null, null));

        Assert.NotNull(result);
        Assert.Equal(6, result!.Orders.Count);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "Selsey" && order.Payload.GetProperty("pallets").GetInt32() == 5);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "Drayton" && order.Payload.GetProperty("pallets").GetInt32() == 15);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "Merston" && order.Payload.GetProperty("pallets").GetInt32() == 7);
        Assert.All(result.Orders, order => Assert.Equal("NWF", order.Payload.GetProperty("customerCode").GetString()));
    }

    [Fact]
    public void CrateComment_IsFlaggedRatherThanSilentlyInterpreted()
    {
        var body = "19/08/2026| PO00500645| SLH1908B| PO00499321| Blackdown and Valefresco| | | | 5| 26| 5| | | Just Crates from Selsey to Valfresco";
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "nwf-2", null, "info@lyonshaulage.com", "ShiftLogisticalPlanner@nwfltd.co.uk", "Shift Logistical Planner",
            "NWAY SLH DAILY TRACKER AMENDED", DateTimeOffset.UtcNow, body, null, null, null));

        var order = Assert.Single(result!.Orders);
        Assert.Equal("Medium", order.Payload.GetProperty("intakeConfidence").GetString());
        Assert.Contains("Crate-return", string.Join(" ", order.Warnings));
    }

    [Fact]
    public void NewAmendmentUsesStableNaturalKeyPerTrackerMovement()
    {
        var body = "19/08/2026| PO00500646| SLH1908C| PO00499127| Hobson Farming| 15| 7| | | 22| 22| | |";
        var first = parser.TryParse(new MailboxEmailIntakeRequest("one", null, null, "ShiftLogisticalPlanner@nwfltd.co.uk", null, "NWF SLH DAILY TRACKER", DateTimeOffset.UtcNow, body, null, null, null));
        var second = parser.TryParse(new MailboxEmailIntakeRequest("two", null, null, "ShiftLogisticalPlanner@nwfltd.co.uk", null, "NWF SLH DAILY TRACKER AMENDED AGAIN", DateTimeOffset.UtcNow, body, null, null, null));
        Assert.Equal(first!.Orders.Select(order => order.NaturalKey), second!.Orders.Select(order => order.NaturalKey));
    }
}
