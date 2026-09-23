using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class FuelCostCalculatorTests
{
    [Fact]
    public void Calculate_ConvertsMilesAndMpgToLitresAndPounds()
    {
        var result = FuelCostCalculator.Calculate(
            distanceMiles: 180m,
            milesPerImperialGallon: 9m,
            pricePencePerLitre: 150m);

        Assert.Equal(90.92m, result.FuelLitres);
        Assert.Equal(136.38m, result.FuelCostPounds);
    }

    [Fact]
    public void Calculate_RejectsInvalidMpg()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => FuelCostCalculator.Calculate(100m, 0m, 150m));
        Assert.Equal("milesPerImperialGallon", error.ParamName);
    }

    [Fact]
    public void Calculate_RejectsNegativeDistance()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => FuelCostCalculator.Calculate(-1m, 9m, 150m));
        Assert.Equal("distanceMiles", error.ParamName);
    }

    [Fact]
    public void Calculate_RejectsNonPositiveFuelPrice()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => FuelCostCalculator.Calculate(100m, 9m, 0m));
        Assert.Equal("pricePencePerLitre", error.ParamName);
    }
}
