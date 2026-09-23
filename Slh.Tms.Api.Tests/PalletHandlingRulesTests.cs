using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PalletHandlingRulesTests
{
    [Theory]
    [InlineData("Morrisons", "Any Site", "Any Depot")]
    [InlineData("Waitrose", "Any Site", "Any Depot")]
    [InlineData("Weightrose", "Any Site", "Any Depot")]
    public void Morrisons_and_waitrose_are_standard(string customer, string collection, string destination)
    {
        var result = PalletHandlingRules.Resolve(customer, collection, destination, null);
        Assert.True(result.IsPallet);
        Assert.Equal("Pallet", result.LoadUnitType);
        Assert.Equal("Standard", result.PalletType);
        Assert.Equal("standard", result.ColourKey);
    }

    [Theory]
    [InlineData("Aldi", "Barefoots", "Aldi Goldthorpe")]
    [InlineData("Aldi", "NWF", "Aldi Cardiff")]
    [InlineData("Aldi", "NWF Site 2", "Aldi Sawley")]
    public void Aldi_from_barefoots_or_nwf_is_euro(string customer, string collection, string destination)
    {
        var result = PalletHandlingRules.Resolve(customer, collection, destination, "Standard pallet");
        Assert.True(result.IsPallet);
        Assert.Equal("Euro", result.PalletType);
        Assert.Equal("euro", result.ColourKey);
    }

    [Fact]
    public void Langmeads_to_aldi_atherstone_is_euro()
    {
        var result = PalletHandlingRules.Resolve("Aldi", "Langmeads", "Aldi Atherstone", null);
        Assert.True(result.IsPallet);
        Assert.Equal("Euro", result.PalletType);
        Assert.Contains("Atherstone", result.RuleSource);
    }

    [Fact]
    public void Ham_farm_to_aldi_atherstone_uses_langmeads_euro_rule()
    {
        var result = PalletHandlingRules.Resolve("Aldi", "Ham Farm", "Atherstone", null);
        Assert.True(result.IsPallet);
        Assert.Equal("Euro", result.PalletType);
        Assert.Equal("euro", result.ColourKey);
        Assert.Contains("Langmeads", result.RuleSource);
    }

    [Theory]
    [InlineData("Tesco")]
    [InlineData("Sainsburys")]
    [InlineData("Aldi Goldthorpe")]
    public void Other_langmeads_work_is_standard(string destination)
    {
        var result = PalletHandlingRules.Resolve(null, "Langmeads", destination, null);
        Assert.True(result.IsPallet);
        Assert.Equal("Standard", result.PalletType);
        Assert.Equal("standard", result.ColourKey);
    }

    [Fact]
    public void Ham_farm_to_other_aldi_depots_uses_langmeads_standard_default()
    {
        var result = PalletHandlingRules.Resolve("Aldi", "Ham Farm", "Swindon", null);
        Assert.True(result.IsPallet);
        Assert.Equal("Standard", result.PalletType);
        Assert.Equal("standard", result.ColourKey);
    }

    [Theory]
    [InlineData("Trays", "Tray", "tray")]
    [InlineData("Crates", "Crate", "crate")]
    [InlineData("Trolleys", "Trolley", "trolley")]
    [InlineData("Mixed", "Mixed", "mixed")]
    public void Non_pallet_units_never_receive_a_pallet_type(string sourceUnit, string expectedType, string expectedColour)
    {
        var result = PalletHandlingRules.Resolve("Aldi", "Barefoots", "Aldi Atherstone", sourceUnit);
        Assert.False(result.IsPallet);
        Assert.Equal(expectedType, result.LoadUnitType);
        Assert.Null(result.PalletType);
        Assert.Equal(expectedColour, result.ColourKey);
    }

    [Fact]
    public void Explicit_euro_is_preserved_when_no_business_rule_matches()
    {
        var result = PalletHandlingRules.Resolve("Other", "Other site", "Other depot", "EURO pallet");
        Assert.True(result.IsPallet);
        Assert.Equal("Euro", result.PalletType);
        Assert.Equal("Source pallet type", result.RuleSource);
    }
}
