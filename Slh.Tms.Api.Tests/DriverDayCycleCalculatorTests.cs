using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverDayCycleCalculatorTests
{
    [Fact]
    public void Planning_day_advances_from_tacho_duties_without_any_tms_run()
    {
        var duties = new[]
        {
            Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z"),
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z"),
            Duty("2026-08-26T05:00:00Z", "2026-08-26T15:00:00Z")
        };

        Assert.Equal(4, DriverDayCycleCalculator.Calculate(new DateOnly(2026, 8, 27), duties));
    }

    [Fact]
    public void Weekend_rest_then_monday_and_tuesday_projects_wednesday_as_day_three()
    {
        var duties = new[]
        {
            Duty("2026-09-04T05:00:00Z", "2026-09-04T15:00:00Z"),
            Duty("2026-09-07T05:00:00Z", "2026-09-07T15:00:00Z"),
            Duty("2026-09-08T05:00:00Z", "2026-09-08T15:00:00Z")
        };

        Assert.Equal(3, DriverDayCycleCalculator.Calculate(new DateOnly(2026, 9, 9), duties));
    }

    [Fact]
    public void Twenty_four_hour_weekly_rest_gap_resets_cycle_to_day_one()
    {
        var duties = new[]
        {
            Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z"),
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z"),
            Duty("2026-08-27T05:00:00Z", "2026-08-27T15:00:00Z")
        };

        Assert.Equal(2, DriverDayCycleCalculator.Calculate(new DateOnly(2026, 8, 28), duties));
    }

    [Fact]
    public void Ongoing_weekly_rest_makes_next_prospective_duty_day_one()
    {
        var duties = new[]
        {
            Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z"),
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z")
        };

        Assert.Equal(1, DriverDayCycleCalculator.Calculate(new DateOnly(2026, 8, 27), duties));
    }

    [Fact]
    public void Overnight_duties_use_the_driver_sign_on_pattern_not_calendar_midnight()
    {
        var duties = new[]
        {
            Duty("2026-08-24T17:00:00Z", "2026-08-25T03:00:00Z"),
            Duty("2026-08-25T17:00:00Z", "2026-08-26T03:00:00Z")
        };

        Assert.Equal(3, DriverDayCycleCalculator.Calculate(new DateOnly(2026, 8, 26), duties));
    }

    [Fact]
    public void Duplicate_tacho_rows_do_not_change_cycle()
    {
        var first = Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z");
        var duties = new[]
        {
            first,
            first,
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z")
        };

        Assert.Equal(3, DriverDayCycleCalculator.Calculate(new DateOnly(2026, 8, 26), duties));
    }

    [Fact]
    public void No_recent_tacho_duty_is_day_one()
    {
        Assert.Equal(1, DriverDayCycleCalculator.Calculate(new DateOnly(2026, 8, 28), Array.Empty<TachoDriverDutyStatus>()));
    }

    [Fact]
    public void Driver_matching_uses_member_code_before_other_identity_fields()
    {
        var driver = new Driver
        {
            EmployeeNumber = "SLH999",
            DisplayName = "Different Name",
            TachoMasterDriverId = "101"
        };

        Assert.True(DriverDayCycleCalculator.MatchesDriver(driver, Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z")));
    }

    private static TachoDriverDutyStatus Duty(string startUtc, string? endUtc) => new(
        "AB12CDE",
        101,
        "Test Driver",
        "1234567890123456",
        "SLH001",
        DateTimeOffset.Parse(startUtc),
        string.IsNullOrWhiteSpace(endUtc) ? null : DateTimeOffset.Parse(endUtc),
        0,
        0,
        0,
        0,
        0,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);
}
