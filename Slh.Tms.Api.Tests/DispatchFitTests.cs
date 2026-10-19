using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DispatchFitTests
{
    [Theory]
    [InlineData("Employed", "Trampers", "Tramper")]
    [InlineData("HalfTramper", null, "HalfTramper")]
    [InlineData("Employed", "Night drivers", "Night")]
    [InlineData("Employed", "Day drivers", "Day")]
    [InlineData("Agency", null, null)]
    public void Working_style_comes_from_driver_master(string type, string? group, string? expected) =>
        Assert.Equal(expected, DriverWorkloadFairness.WorkingStyle(type, group));

    [Fact]
    public void Block_after_a_reduced_weekly_rest_is_five_days()
    {
        Assert.Equal(5, DriverDispatchStatusController.CycleLength("Reduced24"));
        Assert.Equal(6, DriverDispatchStatusController.CycleLength("Regular45"));
        Assert.Equal("Last day — then a full 45h rest", DriverDispatchStatusController.CycleNote(5, 5));
        Assert.Equal("Must take a full 45h weekly rest", DriverDispatchStatusController.CycleNote(6, 5));
        Assert.Null(DriverDispatchStatusController.CycleNote(3, 6));
    }

    [Fact]
    public void Long_work_in_the_last_fortnight_is_counted_per_driver()
    {
        var driver = Guid.NewGuid();
        Load Run(DateOnly date, decimal lat, decimal lon) => new()
        {
            Reference = Guid.NewGuid().ToString("N")[..8], PlanningDate = date, DriverId = driver, Status = LoadStatus.Completed,
            Stops = [new LoadStop { Name = "Deliver", Sequence = 1, Latitude = lat, Longitude = lon }]
        };
        var date = new DateOnly(2026, 10, 19);
        var summary = DriverWorkloadFairness.Summarise([
            Run(date.AddDays(-1), 53.27m, -3.03m),  // Neston ~ 220 miles
            Run(date.AddDays(-2), 50.92m, -0.48m),  // local
            Run(date.AddDays(-20), 53.27m, -3.03m)  // too old
        ], date)[driver];
        Assert.Equal(2, summary.Runs);
        Assert.Equal(1, summary.LongRuns);
    }
}
