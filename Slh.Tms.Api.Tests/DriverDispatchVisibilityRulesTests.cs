using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverDispatchVisibilityRulesTests
{
    private static readonly DateOnly PlanningDate = new(2026, 9, 10);

    [Fact]
    public void Recent_tacho_read_is_visible()
    {
        Assert.True(DriverDispatchVisibilityRules.IsVisible(
            PlanningDate,
            lastTachoRead: PlanningDate.AddDays(-27),
            lastLiveActivity: null,
            lastExecutedRun: null,
            currentlyAllocated: false,
            rosteredAgency: false,
            subcontractor: false));
    }

    [Fact]
    public void Recent_live_activity_is_visible()
    {
        Assert.True(DriverDispatchVisibilityRules.IsVisible(
            PlanningDate,
            lastTachoRead: null,
            lastLiveActivity: new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
            lastExecutedRun: null,
            currentlyAllocated: false,
            rosteredAgency: false,
            subcontractor: false));
    }

    [Fact]
    public void Recent_executed_run_is_visible_as_operational_live_evidence()
    {
        Assert.True(DriverDispatchVisibilityRules.IsVisible(
            PlanningDate,
            lastTachoRead: null,
            lastLiveActivity: null,
            lastExecutedRun: PlanningDate.AddDays(-20),
            currentlyAllocated: false,
            rosteredAgency: false,
            subcontractor: false));
    }

    [Fact]
    public void Old_or_missing_activity_is_hidden()
    {
        Assert.False(DriverDispatchVisibilityRules.IsVisible(
            PlanningDate,
            lastTachoRead: PlanningDate.AddDays(-29),
            lastLiveActivity: new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
            lastExecutedRun: PlanningDate.AddDays(-40),
            currentlyAllocated: false,
            rosteredAgency: false,
            subcontractor: false));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Operational_exceptions_remain_visible(bool allocated, bool rosteredAgency, bool subcontractor)
    {
        Assert.True(DriverDispatchVisibilityRules.IsVisible(
            PlanningDate,
            lastTachoRead: null,
            lastLiveActivity: null,
            lastExecutedRun: null,
            currentlyAllocated: allocated,
            rosteredAgency: rosteredAgency,
            subcontractor: subcontractor));
    }
}