using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class GeofenceHistoryReplayServiceTests
{
    [Fact]
    public void Priority_site_diagnostics_expose_planned_linked_and_departed_counts()
    {
        var result = new GeofenceHistoryReplayResult(
            new DateOnly(2026, 8, 25),
            1250,
            40,
            36,
            30,
            [new("NWF Selsey", 19, 10, 9, 8, DateTimeOffset.Parse("2026-08-25T12:00:00Z"))],
            DateTimeOffset.Parse("2026-08-25T12:01:00Z"));

        var site = Assert.Single(result.PrioritySites);
        Assert.Equal(1250, result.HistoricalTrackingRecords);
        Assert.Equal("NWF Selsey", site.Site);
        Assert.Equal(19, site.PlannedStops);
        Assert.Equal(10, site.Visits);
        Assert.Equal(9, site.LinkedVisits);
        Assert.Equal(8, site.Departures);
    }
}
