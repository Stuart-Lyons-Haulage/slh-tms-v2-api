using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlanningGeographyTests
{
    [Fact]
    public void Selsey_to_Cardiff_is_not_combined_with_Selsey_to_Chelmsford()
    {
        var selsey = new PlanningGeographyPoint(50.735, -0.79);
        var cardiff = new PlanningGeographyPoint(51.4816, -3.1791);
        var chelmsford = new PlanningGeographyPoint(51.7356, 0.4685);

        var cardiffRoute = new PlanningGeographyRoute("SELSEY", "CARDIFF", selsey, cardiff);
        var chelmsfordRoute = new PlanningGeographyRoute("SELSEY", "CHELMSFORD", selsey, chelmsford);

        Assert.False(PlanningGeography.Compatible(cardiffRoute, chelmsfordRoute));
    }

    [Fact]
    public void Same_collection_and_delivery_corridor_can_be_combined()
    {
        var selsey = new PlanningGeographyPoint(50.735, -0.79);
        var portsmouth = new PlanningGeographyPoint(50.8198, -1.088);
        var fareham = new PlanningGeographyPoint(50.851, -1.179);

        var first = new PlanningGeographyRoute("SELSEY", "PORTSMOUTH", selsey, portsmouth);
        var second = new PlanningGeographyRoute("SELSEY", "FAREHAM", selsey, fareham);

        Assert.True(PlanningGeography.Compatible(first, second));
    }

    [Fact]
    public void Unmapped_sites_only_combine_when_both_endpoints_match()
    {
        var first = new PlanningGeographyRoute("UNKNOWN_COLLECTION", "UNKNOWN_DELIVERY", null, null);
        var second = new PlanningGeographyRoute("UNKNOWN_COLLECTION", "OTHER_DELIVERY", null, null);

        Assert.False(PlanningGeography.Compatible(first, second));
    }
}