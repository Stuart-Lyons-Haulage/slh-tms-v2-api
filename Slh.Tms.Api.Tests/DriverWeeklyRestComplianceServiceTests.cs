using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverWeeklyRestComplianceServiceTests
{
    [Fact]
    public void Six_by_twenty_four_window_is_still_available_before_deadline()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-08-20T05:00:00Z", "2026-08-20T15:00:00Z"),
            Duty("2026-08-22T15:00:00Z", "2026-08-22T23:00:00Z"),
            Duty("2026-08-23T05:00:00Z", "2026-08-23T15:00:00Z"),
            Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z"),
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z"),
            Duty("2026-08-26T05:00:00Z", "2026-08-26T15:00:00Z"),
            Duty("2026-08-27T05:00:00Z", "2026-08-27T15:00:00Z")
        };

        var result = DriverWeeklyRestComplianceService.Evaluate(driver, DateTimeOffset.Parse("2026-08-28T14:00:00Z"), duties);

        Assert.NotEqual("Overdue", result.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T15:00:00Z"), result.WeeklyRestDueUtc);
        Assert.Equal("Regular45", result.LastWeeklyRestType);
        Assert.Equal(48, result.LastWeeklyRestHours);
        Assert.Equal(0, result.ReducedRestCompensationHours);
    }

    [Fact]
    public void Late_weekly_rest_start_after_the_144_hour_deadline_is_overdue()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-08-20T05:00:00Z", "2026-08-20T15:00:00Z"),
            Duty("2026-08-22T15:00:00Z", "2026-08-22T23:00:00Z"),
            Duty("2026-08-23T05:00:00Z", "2026-08-23T15:00:00Z"),
            Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z"),
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z"),
            Duty("2026-08-26T05:00:00Z", "2026-08-26T15:00:00Z"),
            Duty("2026-08-27T05:00:00Z", "2026-08-27T15:00:00Z"),
            Duty("2026-08-28T05:00:00Z", "2026-08-28T16:00:00Z"),
            Duty("2026-08-29T16:00:00Z", "2026-08-29T18:00:00Z")
        };

        var result = DriverWeeklyRestComplianceService.Evaluate(driver, DateTimeOffset.Parse("2026-08-29T16:00:00Z"), duties);

        Assert.Equal("Overdue", result.Status);
        Assert.True(result.IsBlocked);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T15:00:00Z"), result.WeeklyRestDueUtc);
    }

    [Fact]
    public void Weekly_rest_started_before_deadline_can_reset_window_after_deadline()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-08-20T05:00:00Z", "2026-08-20T15:00:00Z"),
            Duty("2026-08-22T15:00:00Z", "2026-08-22T23:00:00Z"),
            Duty("2026-08-23T05:00:00Z", "2026-08-23T15:00:00Z"),
            Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z"),
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z"),
            Duty("2026-08-26T05:00:00Z", "2026-08-26T15:00:00Z"),
            Duty("2026-08-27T05:00:00Z", "2026-08-27T15:00:00Z"),
            Duty("2026-08-28T16:00:00Z", "2026-08-28T18:00:00Z")
        };

        var result = DriverWeeklyRestComplianceService.Evaluate(driver, DateTimeOffset.Parse("2026-08-28T16:00:00Z"), duties);

        Assert.Equal("Ready", result.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T16:00:00Z"), result.LastWeeklyRestEndUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-03T16:00:00Z"), result.WeeklyRestDueUtc);
        Assert.Equal("Reduced24", result.LastWeeklyRestType);
        Assert.Equal(25, result.LastWeeklyRestHours);
        Assert.Equal(20, result.ReducedRestCompensationHours);
    }

    [Fact]
    public void Completed_trailing_regular_weekly_rest_resets_window_at_planning_reference()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-08-20T05:00:00Z", "2026-08-20T15:00:00Z"),
            Duty("2026-08-22T15:00:00Z", "2026-08-22T23:00:00Z"),
            Duty("2026-08-23T05:00:00Z", "2026-08-23T15:00:00Z"),
            Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z"),
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z"),
            Duty("2026-08-26T05:00:00Z", "2026-08-26T15:00:00Z"),
            Duty("2026-08-27T05:00:00Z", "2026-08-27T15:00:00Z")
        };
        var referenceUtc = DateTimeOffset.Parse("2026-08-29T14:00:00Z");

        var result = DriverWeeklyRestComplianceService.Evaluate(driver, referenceUtc, duties);

        Assert.Equal("Ready", result.Status);
        Assert.False(result.IsBlocked);
        Assert.Equal(referenceUtc, result.LastWeeklyRestEndUtc);
        Assert.Equal(referenceUtc.AddHours(144), result.WeeklyRestDueUtc);
        Assert.Equal("Regular45", result.LastWeeklyRestType);
        Assert.Equal(47, result.LastWeeklyRestHours);
    }

    [Fact]
    public void Trailing_gap_under_24_hours_does_not_reset_an_overdue_weekly_rest_window()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-08-20T05:00:00Z", "2026-08-20T15:00:00Z"),
            Duty("2026-08-22T15:00:00Z", "2026-08-22T23:00:00Z"),
            Duty("2026-08-23T05:00:00Z", "2026-08-23T15:00:00Z"),
            Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z"),
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z"),
            Duty("2026-08-26T05:00:00Z", "2026-08-26T15:00:00Z"),
            Duty("2026-08-27T05:00:00Z", "2026-08-27T15:00:00Z"),
            Duty("2026-08-28T05:00:00Z", "2026-08-28T15:00:00Z"),
            Duty("2026-08-29T00:00:00Z", "2026-08-29T02:00:00Z")
        };

        var result = DriverWeeklyRestComplianceService.Evaluate(driver, DateTimeOffset.Parse("2026-08-29T20:00:00Z"), duties);

        Assert.Equal("Overdue", result.Status);
        Assert.True(result.IsBlocked);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T15:00:00Z"), result.WeeklyRestDueUtc);
    }

    [Fact]
    public void Twenty_four_hour_gap_is_treated_as_a_reduced_weekly_rest_reset()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-08-20T05:00:00Z", "2026-08-20T15:00:00Z"),
            Duty("2026-08-21T15:00:00Z", "2026-08-21T23:00:00Z"),
            Duty("2026-08-22T23:00:00Z", "2026-08-23T07:00:00Z")
        };

        var result = DriverWeeklyRestComplianceService.Evaluate(driver, DateTimeOffset.Parse("2026-08-23T07:00:00Z"), duties);

        Assert.Equal(DateTimeOffset.Parse("2026-08-22T23:00:00Z"), result.LastWeeklyRestEndUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T23:00:00Z"), result.WeeklyRestDueUtc);
        Assert.Equal("Reduced24", result.LastWeeklyRestType);
        Assert.Equal(24, result.LastWeeklyRestHours);
        // The sample contains two separate 24-hour reduced weekly rests. The evidence total
        // therefore contains 21 hours of visible compensation for each observed reduced rest.
        Assert.Equal(42, result.ReducedRestCompensationHours);
    }

    [Fact]
    public void Missing_anchor_rest_is_unverified_and_does_not_block_driver()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-08-20T05:00:00Z", "2026-08-20T15:00:00Z"),
            Duty("2026-08-21T05:00:00Z", "2026-08-21T15:00:00Z"),
            Duty("2026-08-22T05:00:00Z", "2026-08-22T15:00:00Z"),
            Duty("2026-08-23T05:00:00Z", "2026-08-23T15:00:00Z"),
            Duty("2026-08-24T05:00:00Z", "2026-08-24T15:00:00Z"),
            Duty("2026-08-25T05:00:00Z", "2026-08-25T15:00:00Z"),
            Duty("2026-08-26T05:00:00Z", "2026-08-26T15:00:00Z")
        };

        var result = DriverWeeklyRestComplianceService.Evaluate(driver, DateTimeOffset.Parse("2026-08-26T05:00:00Z"), duties);

        Assert.Equal("Unverified", result.Status);
        Assert.False(result.IsBlocked);
        Assert.Contains("Do not mark this driver unavailable", result.Message);
    }

    [Fact]
    public void Unverified_result_is_advisory_not_a_hard_dispatch_block()
    {
        var result = WeeklyRestComplianceResult.Unverified("history incomplete");

        Assert.False(result.IsBlocked);
    }

    private static Driver TestDriver() => new()
    {
        Id = Guid.NewGuid(),
        EmployeeNumber = "SLH001",
        DisplayName = "Test Driver",
        TachoMasterDriverId = "101"
    };

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