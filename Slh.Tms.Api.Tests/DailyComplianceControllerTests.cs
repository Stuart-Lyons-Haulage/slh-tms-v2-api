using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DailyComplianceControllerTests
{
    [Fact]
    public void Vehicle_aliases_include_registration_suffixes_and_fleet_number()
    {
        var vehicle = new Vehicle
        {
            Registration = "AX19 NFH",
            FleetNumber = "32",
            Abbreviation = "AX19"
        };

        var aliases = DailyComplianceController.VehicleAliases(vehicle);

        Assert.Contains("AX19NFH", aliases);
        Assert.Contains("19NFH", aliases);
        Assert.Contains("32", aliases);
        Assert.Contains("AX19", aliases);
    }

    [Theory]
    [InlineData("BL70 RLO", "BL70RLO")]
    [InlineData("BL70 RLU", "70RLU")]
    [InlineData("CF71 LKG", "CF71LKG")]
    public void Vehicle_matching_handles_tracker_and_tacho_registration_variants(string registration, string providerValue)
    {
        var vehicle = new Vehicle { Registration = registration };

        Assert.True(DailyComplianceController.VehicleMatches(vehicle, providerValue));
    }

    [Theory]
    [InlineData("Daniel Williams", "WILLIAMS, Daniel")]
    [InlineData("John A Smith", "Smith John A")]
    [InlineData("P. Brown", "Brown P")]
    public void Name_matching_handles_provider_name_order_and_punctuation(string tmsName, string providerName)
    {
        Assert.True(DailyComplianceController.NamesEquivalent(tmsName, providerName));
    }

    [Fact]
    public void Fleetio_user_matching_prefers_employee_number_even_when_display_name_differs()
    {
        var driver = new Driver
        {
            EmployeeNumber = "00127",
            DisplayName = "Daniel Williams",
            TachoName = "Williams, Daniel"
        };

        Assert.True(DailyComplianceController.UserMatches("D Williams (Driver)", "00127", driver));
    }

    [Fact]
    public void Fleetio_user_matching_handles_reversed_name_when_employee_number_is_absent()
    {
        var driver = new Driver
        {
            EmployeeNumber = "127",
            DisplayName = "Daniel Williams",
            TachoName = "Williams, Daniel"
        };

        Assert.True(DailyComplianceController.UserMatches("Williams, Daniel", null, driver));
    }

    [Fact]
    public void Name_matching_does_not_match_unrelated_short_names()
    {
        Assert.False(DailyComplianceController.NamesEquivalent("Dan W", "David White"));
    }
}
