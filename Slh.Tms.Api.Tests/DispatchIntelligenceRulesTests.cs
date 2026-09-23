using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DispatchIntelligenceRulesTests
{
    [Fact]
    public async Task MarketRun_named_skill_is_accepted_for_market_run_suggestion()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"dispatch-intelligence-{Guid.NewGuid():N}")
            .Options;
        await using var db = new TmsDbContext(options);

        var driver = TestDriver("MarketRun");
        var run = new Load
        {
            Id = Guid.NewGuid(),
            Reference = "PM Market Run 1",
            PlanningDate = new DateOnly(2026, 9, 10),
            Status = LoadStatus.Draft,
            Stops =
            [
                new LoadStop { Sequence = 1, Name = "Collect · Barnham", Latitude = 50.84m, Longitude = -0.64m },
                new LoadStop { Sequence = 2, Name = "Deliver · New Spitalfields Market", Latitude = 51.57m, Longitude = 0.01m }
            ]
        };

        var result = await DriverDispatchAssistantService.BuildAsync(
            db,
            run.PlanningDate,
            [driver],
            [run],
            [],
            [],
            [],
            new HashSet<Guid>(),
            CancellationToken.None);

        Assert.True(result.TryGetValue(driver.Id, out var suggestion));
        Assert.Equal(run.Id, suggestion!.LoadId);
    }

    [Fact]
    public void Skill_gate_reports_missing_skills_by_name()
    {
        var held = DispatchSkillRules.Parse("MKT, ADR");
        var required = DispatchSkill.MarketRun | DispatchSkill.DoubleDecker;

        Assert.False(DispatchSkillRules.HasAll(held, required));
        Assert.Equal("DoubleDecker", DispatchSkillRules.MissingNames(held, required));
    }

    [Fact]
    public void Required_rest_defaults_to_eleven_hours_even_when_reduced_rest_allowance_remains()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-09-06T05:00:00Z", "2026-09-06T17:00:00Z"),
            Duty("2026-09-07T02:30:00Z", "2026-09-07T15:00:00Z", shortDailyRestsUsed: 1)
        };

        var result = DispatchTachoRules.DeriveRequiredRestPeriod(driver, duties);

        Assert.Equal(11, result.Hours);
        Assert.Equal(1, result.ReducedDailyRestsUsed);
    }

    [Fact]
    public void Required_rest_is_nine_hours_only_when_planner_explicitly_selects_reduced_rest()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-09-06T05:00:00Z", "2026-09-06T17:00:00Z"),
            Duty("2026-09-07T02:30:00Z", "2026-09-07T15:00:00Z", shortDailyRestsUsed: 1)
        };

        var result = DispatchTachoRules.DeriveRequiredRestPeriod(driver, duties, useReducedDailyRest: true);

        Assert.Equal(9, result.Hours);
        Assert.Equal(1, result.ReducedDailyRestsUsed);
    }

    [Fact]
    public void Required_rest_stays_eleven_hours_when_reduced_rest_allowance_is_exhausted()
    {
        var driver = TestDriver();
        var duties = new[]
        {
            Duty("2026-09-04T05:00:00Z", "2026-09-04T17:00:00Z"),
            Duty("2026-09-05T02:00:00Z", "2026-09-05T14:00:00Z"),
            Duty("2026-09-05T23:00:00Z", "2026-09-06T11:00:00Z"),
            Duty("2026-09-06T20:00:00Z", "2026-09-07T08:00:00Z", shortDailyRestsUsed: 3)
        };

        var result = DispatchTachoRules.DeriveRequiredRestPeriod(driver, duties, useReducedDailyRest: true);

        Assert.Equal(11, result.Hours);
        Assert.Equal(3, result.ReducedDailyRestsUsed);
    }

    [Theory]
    [InlineData(4, 53.10, true)]
    [InlineData(5, 52.51, true)]
    [InlineData(3, 53.10, false)]
    [InlineData(5, 52.40, false)]
    public void Needs_return_requires_day_four_or_later_and_northern_position(int day, double latitude, bool expected)
    {
        Assert.Equal(expected, DispatchReturnRules.NeedsReturn(day, (decimal)latitude, 52.5m));
    }

    [Fact]
    public void Wtd_breach_reports_when_during_the_run_the_limit_is_crossed()
    {
        var breach = DispatchTachoRules.DetectProjectedBreach(
            weeklyWorkingTimeHours: 58m,
            dailyDrivingTimeHours: 0m,
            projectedDutyHours: 4m,
            projectedDrivingHours: 2m,
            dailyDrivingLimitHours: 9m);

        Assert.NotNull(breach);
        Assert.Equal("WTD", breach!.Code);
        Assert.Equal(2m, breach.HoursIntoRun);
        Assert.Equal("WTD breach — 2h into this run", breach.Detail);
    }

    [Fact]
    public void Available_from_adds_the_rest_period_to_tacho_shift_end()
    {
        var shiftEnd = DateTimeOffset.Parse("2026-09-09T18:00:00Z");
        var requirement = new DispatchRestRequirement(9, 1, "Planner selected reduced daily rest");

        var result = DispatchTachoRules.AvailableFrom(shiftEnd, requirement);

        Assert.Equal(DateTimeOffset.Parse("2026-09-10T03:00:00Z"), result);
    }

    [Theory]
    [InlineData(39.9, "ok")]
    [InlineData(40, "amber")]
    [InlineData(47.9, "amber")]
    [InlineData(48, "red")]
    public void Wtd_status_matches_dispatch_colour_thresholds(double hours, string expected)
    {
        Assert.Equal(expected, DispatchTachoRules.WtdStatus((decimal)hours));
    }

    private static Driver TestDriver(string? skills = null) => new()
    {
        Id = Guid.NewGuid(),
        EmployeeNumber = "SLH001",
        DisplayName = "Test Driver",
        DriverType = "Employed",
        TachoMasterDriverId = "101",
        TachoCardNumber = "1234567890123456",
        Skills = skills,
        Active = true
    };

    private static TachoDriverDutyStatus Duty(string startUtc, string endUtc, int? shortDailyRestsUsed = null) => new(
        VehicleCode: "AB12CDE",
        MemberCode: 101,
        DriverName: "Test Driver",
        CardNumber: "1234567890123456",
        EmployeeNumber: "SLH001",
        DutyStartUtc: DateTimeOffset.Parse(startUtc),
        DutyEndUtc: DateTimeOffset.Parse(endUtc),
        WorkMinutes: 180,
        RestMinutes: 0,
        AvailableMinutes: 0,
        DriveMinutes: 300,
        BreakCount: 1,
        BreakMinutes: 45,
        MetricsValidAtUtc: DateTimeOffset.Parse(endUtc),
        DailyDriverPeriodsAvailable: 1,
        DriveAvailableTodayMinutes: 240,
        DriveAvailableTomorrowMinutes: 540,
        DriveAvailableWeekMinutes: 1200,
        DriveAvailableFortnightMinutes: 2400,
        LongDaysWorkedThisWeek: 0,
        ShortDailyRestTakenThisWeek: shortDailyRestsUsed,
        WorkAvailableWeekMinutes: 1200);
}
