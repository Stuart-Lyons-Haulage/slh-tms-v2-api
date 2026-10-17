using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class NwfQuantityChangeParserTests
{
    private readonly NwfQuantityChangeParser parser = new();

    [Fact]
    public void BedfordDeliveryQuantityChange_StagesDepotSplitRows()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "message-nwf-bedford-change",
            null,
            "info@lyonshaulage.com",
            "MalwinaFrasek@nwfltd.co.uk",
            "Malwina Frasek",
            "Change in Delivery Quantities - Order Ref Bedford 228135851",
            DateTimeOffset.Parse("2026-08-25T08:27:12Z"),
            """
            Good Morning

            Please note there is a change to the delivery quantities for order ref. Bedford 228135851

            Please deliver:
            * 23 pallets to Merston
            * 10 pallets to Runcton

            ALDI | PO00501442 | £311.52 | 25/08/2026 | 26/08/2026 | 22 | Bedford | 228135851 | PO00500623 | Runcton/Merston | 33 | 23 Merston & 10 Runcton
            Natures Way Foods Ltd
            """,
            null,
            null,
            null));

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(2, result.Orders.Count);

        var merston = Assert.Single(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "Merston");
        Assert.Equal("NWF", merston.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("Bedford", merston.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("2026-08-26", merston.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal(23, merston.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("NWF Quantity Change", merston.Payload.GetProperty("intakeParser").GetString());

        var runcton = Assert.Single(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "Runcton");
        Assert.Equal(10, runcton.Payload.GetProperty("pallets").GetInt32());

        var matchKeys = merston.Payload.GetProperty("intakeMatchKeys").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Contains("NWF|2026-08-26|PRODUCT:PO00500623", matchKeys);
        Assert.Contains("NWF|2026-08-26|TRANSPORT:PO00501442", matchKeys);
        Assert.Contains("NWF|2026-08-26|LOADING:BEDFORD", matchKeys);
    }
}
