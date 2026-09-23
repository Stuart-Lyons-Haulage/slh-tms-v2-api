using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class LoadUtilisationTests
{
    [Fact]
    public void CalculatesUtilisationFromUsedAndAvailablePalletSpaces()
    {
        var load = new Load
        {
            Reference = "SLH1208A",
            PlanningDate = new DateOnly(2026, 8, 12),
            PalletSpacesUsed = 18,
            TotalPalletSpaces = 26,
            CapacityType = "Standard pallets"
        };

        Assert.Equal(69.2m, load.UtilisationPercent);
    }

    [Fact]
    public void LeavesUtilisationBlankUntilCapacityIsKnown()
    {
        var load = new Load
        {
            Reference = "SLH1208B",
            PlanningDate = new DateOnly(2026, 8, 12),
            PalletSpacesUsed = 8
        };

        Assert.Null(load.UtilisationPercent);
    }

    [Fact]
    public void ShowsOverCapacityAsAVisiblePercentage()
    {
        var load = new Load
        {
            Reference = "SLH1208C",
            PlanningDate = new DateOnly(2026, 8, 12),
            PalletSpacesUsed = 27,
            TotalPalletSpaces = 26
        };

        Assert.Equal(103.8m, load.UtilisationPercent);
    }
}
