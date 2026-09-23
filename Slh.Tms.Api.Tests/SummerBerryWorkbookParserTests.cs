using IntakeParser = Slh.Tms.Api.Controllers.SpecialistMailboxOrderParser;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SummerBerryWorkbookParserTests
{
    [Theory]
    [InlineData("MORRISONS SITTINGBOURNE", "MORRISONS")]
    [InlineData("ALDI GOLDTHORPE", "ALDI")]
    public void Retailer_destination_keeps_summer_berry_as_primary_customer(string depot, string retailer)
    {
        var identity = IntakeParser.SummerBerryIdentity(depot);

        Assert.NotNull(identity);
        Assert.Equal("SUMMERBERRY", identity.Value.CustomerCode);
        Assert.Equal(retailer, identity.Value.RetailerCode);
    }

    [Fact]
    public void Unsupported_retailer_is_not_classified_as_summer_berry_simplified_lane()
    {
        Assert.Null(IntakeParser.SummerBerryIdentity("TESCO DAVENTRY"));
    }
}
