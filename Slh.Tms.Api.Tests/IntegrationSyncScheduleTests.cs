using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class IntegrationSyncScheduleTests
{
    [Fact]
    public void Fleetio_runs_at_the_next_uk_hour_boundary()
    {
        var now = new DateTimeOffset(2026, 1, 15, 12, 37, 0, TimeSpan.Zero);

        var next = IntegrationSyncSchedule.NextFleetio(now);

        Assert.Equal(new DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Sage_runs_at_05_and_14_uk_time_with_dst_conversion()
    {
        var beforeMorning = new DateTimeOffset(2026, 1, 15, 4, 30, 0, TimeSpan.Zero);
        var beforeAfternoon = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var summer = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 1, 15, 5, 0, 0, TimeSpan.Zero), IntegrationSyncSchedule.NextSageHr(beforeMorning));
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 14, 0, 0, TimeSpan.Zero), IntegrationSyncSchedule.NextSageHr(beforeAfternoon));
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 13, 0, 0, TimeSpan.Zero), IntegrationSyncSchedule.NextSageHr(summer));
    }
}
