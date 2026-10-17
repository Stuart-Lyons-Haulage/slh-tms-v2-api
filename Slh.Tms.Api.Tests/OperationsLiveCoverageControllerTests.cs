using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationsLiveCoverageControllerTests
{
    [Fact]
    public void Vehicle_tacho_match_aliases_include_master_fleet_number_when_dot_uses_registration()
    {
        var aliases = OperationsLiveCoverageController.VehicleTachoMatchAliases(
            "AX19 NFH",
            "AX19NFH",
            "32",
            "AX19");

        Assert.Contains("AX19NFH", aliases);
        Assert.Contains("19NFH", aliases);
        Assert.Contains("32", aliases);
        Assert.Contains("AX19", aliases);
    }
}
