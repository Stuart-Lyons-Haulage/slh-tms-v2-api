using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SamsaraLocationRulesTests
{
    [Theory]
    [InlineData(50.8, -0.1, true)]
    [InlineData(0.0, 0.0, false)]
    [InlineData(91.0, 0.0, false)]
    [InlineData(50.8, 181.0, false)]
    public void Coordinate_validation_rejects_zero_placeholder_and_out_of_range_points(double latitude, double longitude, bool expected)
    {
        Assert.Equal(expected, SamsaraLocationRules.HasUsableCoordinates(latitude, longitude));
    }

    [Fact]
    public void Numeric_transport_reference_uses_matched_site_physical_address()
    {
        var address = SamsaraLocationRules.ResolvePhysicalAddress(
            "1000001350088",
            "Unit 4, Industrial Estate, Portsmouth, PO3 5XX",
            "Customer site");

        Assert.Equal("Unit 4, Industrial Estate, Portsmouth, PO3 5XX", address);
    }

    [Fact]
    public void Numeric_transport_reference_without_site_address_is_not_sent_as_a_location()
    {
        var address = SamsaraLocationRules.ResolvePhysicalAddress(
            "1000001350088",
            null,
            "1000001350088");

        Assert.Equal(string.Empty, address);
        Assert.False(SamsaraLocationRules.IsUsablePhysicalAddress(address));
    }

    [Fact]
    public void Explicit_physical_stop_address_takes_precedence_over_site_default()
    {
        var address = SamsaraLocationRules.ResolvePhysicalAddress(
            "Loading Bay, 9 Harbour Road, Portsmouth, PO6 4TS",
            "9 Harbour Road, Portsmouth, PO6 4TS",
            "Customer site");

        Assert.Equal("Loading Bay, 9 Harbour Road, Portsmouth, PO6 4TS", address);
    }
}
