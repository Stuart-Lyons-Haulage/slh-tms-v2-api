using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverWorkPatternsTests
{
    private static DriverWorkPatterns.Pattern Pattern(string blocks, DateOnly start) =>
        new(Guid.NewGuid(), "5 on 2 off, 6 on 1 off", blocks, start, DriverWorkPatterns.SourceSageEntered, null, DateTimeOffset.UtcNow);

    [Fact]
    public void Five_two_six_one_cycle_gives_day_of_block_and_rest_days()
    {
        // Cycle starts Monday 5 Oct 2026: Mon-Fri on, Sat-Sun off, Mon-Sat on, Sun off.
        var pattern = Pattern("5-2,6-1", new DateOnly(2026, 10, 5));
        Assert.Equal(new DriverWorkPatterns.DayPlan(true, 1, 5, 14), DriverWorkPatterns.Evaluate(pattern, new DateOnly(2026, 10, 5)));
        Assert.Equal(new DriverWorkPatterns.DayPlan(true, 5, 5, 14), DriverWorkPatterns.Evaluate(pattern, new DateOnly(2026, 10, 9)));
        Assert.False(DriverWorkPatterns.Evaluate(pattern, new DateOnly(2026, 10, 10))!.Working);
        Assert.Equal(new DriverWorkPatterns.DayPlan(true, 6, 6, 14), DriverWorkPatterns.Evaluate(pattern, new DateOnly(2026, 10, 17)));
        Assert.False(DriverWorkPatterns.Evaluate(pattern, new DateOnly(2026, 10, 18))!.Working);
        // Repeats: Monday 19 Oct is Day 1 of 5 again, and it works before the start date too.
        Assert.Equal(1, DriverWorkPatterns.Evaluate(pattern, new DateOnly(2026, 10, 19))!.Day);
        Assert.Equal(6, DriverWorkPatterns.Evaluate(pattern, new DateOnly(2026, 10, 3))!.Day);
    }

    [Fact]
    public void Weekday_patterns_and_bad_text()
    {
        var pattern = Pattern("Mon,Tue,Wed,Thu,Fri", new DateOnly(2026, 1, 1));
        Assert.Equal(3, DriverWorkPatterns.Evaluate(pattern, new DateOnly(2026, 10, 21))!.Day);
        Assert.False(DriverWorkPatterns.Evaluate(pattern, new DateOnly(2026, 10, 24))!.Working);
        Assert.Null(DriverWorkPatterns.ParseCycle("5 on 2 off", out _));
        Assert.Null(DriverWorkPatterns.ParseCycle("7-0", out _));
        Assert.Equal("5 on 2 off, then 6 on 1 off", DriverWorkPatterns.Describe("5-2,6-1"));
    }

    [Fact]
    public void Tacho_overrides_the_pattern_for_what_is_legal()
    {
        var plan = new DriverWorkPatterns.DayPlan(true, 5, 6, 14);
        var reduced = WorkPatternFit.Resolve(plan, "P", 5, 5, "Reduced24");
        Assert.Equal(5, reduced.DisplayDay);
        Assert.Equal(6, reduced.BlockLength);
        Assert.Contains("45h", reduced.Note);

        var extra = WorkPatternFit.Resolve(new DriverWorkPatterns.DayPlan(true, 2, 5, 14), "P", 4, 6, "Regular45");
        Assert.Equal(4, extra.DisplayDay);
        Assert.Contains("Tacho shows Day 4", extra.Note);

        var rest = WorkPatternFit.Resolve(new DriverWorkPatterns.DayPlan(false, null, null, 14), "5 on 2 off", 3, 6, "Regular45");
        Assert.True(rest.RestDay);

        var none = WorkPatternFit.Resolve(null, null, 3, 6, "Regular45");
        Assert.Null(none.DisplayDay);
        Assert.Contains("Tacho", none.Note);
    }
}
