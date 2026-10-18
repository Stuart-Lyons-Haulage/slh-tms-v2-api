using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class HandlingUnitClassifierTests
{
    [Fact]
    public void Quantities_are_read_per_unit_and_pallets_of_trays_count_as_trays()
    {
        Assert.Equal(new[] { ("Pallets", 10), ("Trolleys", 4) }, HandlingUnitClassifier.UnitQuantities("Please collect 10 pallets + 4 trolleys from Hall Hunter"));
        Assert.Equal(new[] { ("Trays", 26) }, HandlingUnitClassifier.UnitQuantities("Please collect from sefter 26 pallets of rental trays on 19.10"));
        Assert.Empty(HandlingUnitClassifier.UnitQuantities("Kind regards, Tray Controller"));
    }

    [Fact]
    public void Tray_collection_is_labelled_and_grouped_as_a_southbound_collection()
    {
        var order = new ParsedEmailOrder("k", "n", JsonSerializer.SerializeToElement(new
        {
            customerCode = "ALDI", pallets = 33, sellerName = "Bedford", stallNumber = "Selsey",
            jobType = "NWF confirmed ALDI tray collection"
        }), []);

        var labelled = HandlingUnitClassifier.Apply(order).Payload;

        Assert.Equal("Trays", labelled.GetProperty("palletType").GetString());
        Assert.True(labelled.GetProperty("southboundCollection").GetBoolean());
        Assert.Equal(HandlingUnitClassifier.SouthboundGroup, labelled.GetProperty("planningGroup").GetString());
        Assert.Equal(33, labelled.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void Trolley_order_keeps_its_trolley_count_and_pallet_orders_are_untouched()
    {
        var trolleys = HandlingUnitClassifier.Apply(new ParsedEmailOrder("k", "n", JsonSerializer.SerializeToElement(new
        {
            customerCode = "WAITROSE", pallets = 21, jobType = "Waitrose depot delivery (trolleys)"
        }), [])).Payload;
        Assert.Equal("Trolleys", trolleys.GetProperty("palletType").GetString());
        Assert.Equal(21, trolleys.GetProperty("trolleys").GetInt32());
        Assert.False(trolleys.TryGetProperty("planningGroup", out _));

        var pallets = new ParsedEmailOrder("k", "n", JsonSerializer.SerializeToElement(new { customerCode = "ALDI", pallets = 14, jobType = "Barfoots Aldi confirmed workbook" }), []);
        Assert.Equal(pallets.Payload.GetRawText(), HandlingUnitClassifier.Apply(pallets).Payload.GetRawText());
    }

    [Fact]
    public void Double_H_trolleys_reach_review_as_trolleys()
    {
        var parsed = new EmailOrderIntakeService().Parse(new MailboxEmailIntakeRequest(
            "message-doubleh-trolleys", null, "info@lyonshaulage.com", "Ramas@doubleh.co.uk", "Ramas",
            "Waitrose order for dd 27.08 from Double H", DateTimeOffset.Parse("2026-08-26T07:07:37Z"),
            """
            Please see below Waitrose order for depot date 27.08 (collection today 26.08)

            Depot date| 27.08| | | |
             | Aylesford| Bracknell| Brinklow| Leyland|
            Order Ref| X58520| J58713| R58625| Z57428| TOTAL
            Trolleys| 21| 40| 39| 23| 123
            """, null, null, null));

        var labelled = HandlingUnitClassifier.Apply(parsed);
        Assert.Equal(4, labelled.Orders.Count);
        Assert.All(labelled.Orders, order => Assert.Equal("Trolleys", order.Payload.GetProperty("palletType").GetString()));
        Assert.Contains(labelled.Orders, order => order.Payload.GetProperty("trolleys").GetInt32() == 40);
    }
}
