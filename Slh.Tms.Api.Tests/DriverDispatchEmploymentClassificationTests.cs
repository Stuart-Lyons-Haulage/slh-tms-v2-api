using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverDispatchEmploymentClassificationTests
{
    [Fact]
    public void Driver_Master_employed_classification_is_preserved()
    {
        var driver = new Driver { EmployeeNumber = "728", DriverType = "Employed", DisplayName = "Sage Driver" };
        var roster = new DriverDispatchVisibilityStore.SageRoster(true, new HashSet<string> { "728" });

        Assert.Equal("Employed", DriverDispatchVisibilityStore.EmploymentType(driver, roster));
    }

    [Fact]
    public void Sage_mismatch_does_not_reclassify_an_employed_master_record()
    {
        var driver = new Driver { EmployeeNumber = "999", DriverType = "Employed", DisplayName = "Unmatched Driver" };
        var roster = new DriverDispatchVisibilityStore.SageRoster(true, new HashSet<string> { "728" });

        Assert.Equal("Employed", DriverDispatchVisibilityStore.EmploymentType(driver, roster));
    }

    [Fact]
    public void Agency_is_read_from_Master_Data_and_roster_does_not_promote_it()
    {
        var agency = new Driver { EmployeeNumber = "AG1", DriverType = "Agency", DisplayName = "Agency Driver" };
        var subcontractor = new Driver { EmployeeNumber = "SUB-1", DriverType = "Subcontractor", DisplayName = "Subbie" };
        var roster = new DriverDispatchVisibilityStore.SageRoster(true, new HashSet<string>());

        Assert.Equal("Agency", DriverDispatchVisibilityStore.EmploymentType(agency, roster));
        Assert.Equal("Agency", DriverDispatchVisibilityStore.EmploymentType(agency, roster, rosteredAgency: true));
        Assert.Equal("Unknown", DriverDispatchVisibilityStore.EmploymentType(subcontractor, roster));
    }
}
