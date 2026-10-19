using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SamsaraReconciliationTests
{
    [Fact]
    public void Driver_names_match_in_either_order_and_with_middle_names()
    {
        Assert.True(SamsaraReconciliation.SameName("Smith Adam", "Adam Douglas Smith"));
        Assert.True(SamsaraReconciliation.SameName("Barry King", "barry king"));
        Assert.False(SamsaraReconciliation.SameName("Adam Smith", "Adam Czarnecki"));
    }

    [Fact]
    public void Gaps_are_listed_both_ways_and_matches_are_linked()
    {
        var tms = new List<Driver>
        {
            new() { EmployeeNumber = "101", DisplayName = "Smith Adam" },
            new() { EmployeeNumber = "102", DisplayName = "Barry King" },
            new() { EmployeeNumber = "103", DisplayName = "Only In Tms" },
        };
        var samsara = new List<SamsaraDriver>
        {
            new("s1", "Adam Douglas Smith", "asmith", null, new Dictionary<string, string>()),
            new("s2", "B King", "bking", null, new Dictionary<string, string> { ["payroll"] = "102" }),
            new("s3", "Left Company", "left", null, new Dictionary<string, string>()),
        };
        var linked = new List<string>();

        var section = SamsaraReconciliation.Compare(tms, samsara, driver => driver.DisplayName, remote => remote.Name!,
            SamsaraReconciliation.DriverMatches, (driver, remote) => SamsaraReconciliation.SameName(driver.DisplayName, remote.Name) ? null : "name differs",
            (driver, remote) => linked.Add(remote.Id));

        Assert.Equal(2, section.Matched);
        Assert.Equal(["s1", "s2"], linked);
        Assert.Equal("Only In Tms", Assert.Single(section.OnlyInTms).Name);
        Assert.Equal("Left Company", Assert.Single(section.OnlyInSamsara).Name);
        Assert.Equal("Barry King", Assert.Single(section.Differences).Name);
    }

    [Fact]
    public void Vehicles_match_on_registration_ignoring_spaces()
    {
        Assert.True(SamsaraReconciliation.VehicleMatches("GN19 ABC", null, "GN19ABC", null, null));
        Assert.True(SamsaraReconciliation.VehicleMatches("GN19ABC", null, null, "GN19 ABC - Scania", null));
        Assert.False(SamsaraReconciliation.VehicleMatches("GN19ABC", null, "GN19ABD", "Other", null));
    }
}
