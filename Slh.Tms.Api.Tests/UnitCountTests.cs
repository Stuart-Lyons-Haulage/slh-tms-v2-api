using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class UnitCountTests
{
    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Euro_pool_order_keeps_pallets_and_tray_count_apart()
    {
        var count = HandlingUnitClassifier.UnitCount(Payload(new { pallets = 33, handlingUnitType = "Euro Pool Trays", handlingUnitQuantity = 8316 }));
        Assert.Equal((8316, "Trays"), count);
    }

    [Fact]
    public void Tray_count_in_the_text_is_found_but_pallets_of_trays_is_not_a_tray_count()
    {
        Assert.Equal((1200, "Crates"), HandlingUnitClassifier.UnitCount(Payload(new { pallets = 0, driverInstructions = "Collect 1,200 IFCO crates from Glasshoughton" })));
        Assert.Equal((110, "Shippers"), HandlingUnitClassifier.UnitCount(Payload(new { pallets = 0, sourceBodyText = "Please collect 110 potato shippers from Basingstoke" })));
        Assert.Null(HandlingUnitClassifier.UnitCount(Payload(new { pallets = 26, driverInstructions = "26 pallets of rental trays" })));
    }

    [Fact]
    public void Plain_pallet_order_has_no_unit_count()
    {
        Assert.Null(HandlingUnitClassifier.UnitCount(Payload(new { pallets = 12, handlingUnitType = "Pallets", handlingUnitQuantity = 12 })));
    }
}
