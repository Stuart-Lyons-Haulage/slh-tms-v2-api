using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverDispatchEmploymentClassificationTests
{
    [Fact]
    public void Only_a_driver_matched_to_the_Sage_roster_is_employed()
    {
        var driver = new Driver { EmployeeNumber = "728", DriverType = "Employed", DisplayName = "Sage Driver" };
        var roster = new DriverDispatchVisibilityStore.SageRoster(true, new HashSet<string> { "728" });

        Assert.Equal("Employed", DriverDispatchVisibilityStore.EmploymentType(driver, roster));
    }

    [Fact]
    public void Local_employed_label_without_Sage_match_is_not_promoted()
    {
        var driver = new Driver { EmployeeNumber = "999", DriverType = "Employed", DisplayName = "Unmatched Driver" };
        var roster = new DriverDispatchVisibilityStore.SageRoster(true, new HashSet<string> { "728" });

        Assert.Equal("Unmatched", DriverDispatchVisibilityStore.EmploymentType(driver, roster));
    }

    [Fact]
    public void Agency_and_subcontractor_categories_are_preserved_outside_Sage()
    {
        var agency = new Driver { EmployeeNumber = "AG1", DriverType = "Agency", DisplayName = "Agency Driver" };
        var subcontractor = new Driver { EmployeeNumber = "SUB-1", DriverType = "Subcontractor", DisplayName = "Subbie" };
        var roster = new DriverDispatchVisibilityStore.SageRoster(true, new HashSet<string>());

        Assert.Equal("Unmatched", DriverDispatchVisibilityStore.EmploymentType(agency, roster));
        Assert.Equal("Agency", DriverDispatchVisibilityStore.EmploymentType(agency, roster, rosteredAgency: true));
        Assert.Equal("Subcontractor", DriverDispatchVisibilityStore.EmploymentType(subcontractor, roster));
    }
}
