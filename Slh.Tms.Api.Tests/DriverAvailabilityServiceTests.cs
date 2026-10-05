using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverAvailabilityServiceTests
{
    private static readonly DateOnly Day = new(2026, 10, 6);
    private static readonly DriverAvailabilityDetail EmptyDetail = DriverAvailabilityDetail.Empty;

    [Fact]
    public void Master_Data_remains_authority_when_Sage_does_not_match()
    {
        var driver = Driver("999", "Employed");
        var item = DriverAvailabilityService.Evaluate(driver, Day, [], [], EmptyDetail, new(true, new HashSet<string> { "728" }));

        Assert.Equal("Employed", item.EmploymentType);
        Assert.True(item.Dispatchable);
        Assert.True(item.ClassificationMismatch);
        Assert.Contains("SageHR", item.ClassificationReviewReason);
    }

    [Fact]
    public void Agency_requires_a_confirmed_window()
    {
        var driver = Driver("AG12", "Agency");
        var unconfirmed = Window(driver, confirmed: false);
        var item = DriverAvailabilityService.Evaluate(driver, Day, [unconfirmed], [], EmptyDetail, SageRosterEvidence.Unavailable);

        Assert.Equal("Agency unconfirmed", item.Group);
        Assert.False(item.Dispatchable);
        Assert.Contains("No confirmed availability", item.BlockReasons);
    }

    [Fact]
    public void Casual_is_dispatchable_only_inside_an_explicit_confirmed_window()
    {
        var driver = Driver("CAS1", "Casual");
        var item = DriverAvailabilityService.Evaluate(driver, Day, [Window(driver, confirmed: true)], [], EmptyDetail, SageRosterEvidence.Unavailable);

        Assert.Equal("Casual confirmed", item.Group);
        Assert.True(item.Dispatchable);
        Assert.NotNull(item.AvailableFromUtc);
        Assert.NotNull(item.AvailableUntilUtc);
    }

    [Fact]
    public void Long_term_agency_pattern_only_repeats_on_usual_days_before_placement_end()
    {
        var driver = Driver("AG13", "Agency");
        var window = Window(driver, confirmed: true);
        window.LongTermPlacement = true;
        window.PlacementEndDate = new DateOnly(2026, 10, 31);
        window.UsualDays = "Mon,Wed,Fri";

        var tuesday = DriverAvailabilityService.Evaluate(driver, Day, [window], [], EmptyDetail, SageRosterEvidence.Unavailable);
        var wednesday = DriverAvailabilityService.Evaluate(driver, Day.AddDays(1), [window], [], EmptyDetail, SageRosterEvidence.Unavailable);

        Assert.False(tuesday.Dispatchable);
        Assert.True(wednesday.Dispatchable);
        Assert.Equal("Agency confirmed", wednesday.Group);
    }

    [Fact]
    public void Existing_allocation_is_an_explicit_block_reason()
    {
        var driver = Driver("728", "Employed");
        var load = new Load { Reference = "AM 1", PlanningDate = Day, DriverId = driver.Id };
        var item = DriverAvailabilityService.Evaluate(driver, Day, [], [load], EmptyDetail, SageRosterEvidence.Unavailable);

        Assert.False(item.Dispatchable);
        Assert.Equal("Unavailable/blocked", item.Group);
        Assert.Contains(item.BlockReasons, reason => reason.Contains("Already allocated to AM 1"));
    }

    [Fact]
    public void Sequential_runs_assigned_to_the_same_driver_require_one_driver()
    {
        var driverId = Guid.NewGuid();
        var loads = new[]
        {
            new Load { Reference = "AM 1", PlanningDate = Day, DriverId = driverId },
            new Load { Reference = "AM 4", PlanningDate = Day, DriverId = driverId }
        };

        Assert.Equal(1, DriverAvailabilityService.RequiredDriverCount(loads));
    }

    [Fact]
    public void Unassigned_runs_each_retain_a_driver_demand_slot()
    {
        var loads = new[]
        {
            new Load { Reference = "AM 1", PlanningDate = Day },
            new Load { Reference = "AM 2", PlanningDate = Day }
        };

        Assert.Equal(2, DriverAvailabilityService.RequiredDriverCount(loads));
    }

    [Fact]
    public void Relay_with_a_different_delivery_driver_requires_both_drivers()
    {
        var load = new Load
        {
            Reference = "TRANSFER 1",
            PlanningDate = Day,
            DriverId = Guid.NewGuid(),
            RelayPlan = new LoadRelayPlan
            {
                Enabled = true,
                DeliveryDriverId = Guid.NewGuid()
            }
        };

        Assert.Equal(2, DriverAvailabilityService.RequiredDriverCount([load]));
    }

    private static Driver Driver(string employeeNumber, string type) => new()
    {
        Id = Guid.NewGuid(),
        EmployeeNumber = employeeNumber,
        DisplayName = $"{type} Driver",
        DriverType = type
    };

    private static DriverAvailabilityWindow Window(Driver driver, bool confirmed) => new()
    {
        DriverId = driver.Id,
        AvailableFromUtc = DateTimeOffset.Parse("2026-10-06T05:00:00Z"),
        AvailableUntilUtc = DateTimeOffset.Parse("2026-10-06T17:00:00Z"),
        Confirmed = confirmed,
        CreatedBy = "test",
        UpdatedBy = "test"
    };
}
