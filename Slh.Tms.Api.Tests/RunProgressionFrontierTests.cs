using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RunProgressionFrontierTests
{
    [Fact]
    public void Later_completed_stops_do_not_send_route_back_to_missing_earlier_stop()
    {
        var selsey = Stop(1, "NWF-Selsey");
        var merston = Stop(2, "NWF-Merston");
        var runcton = Stop(3, "NWF-Runcton");
        var gadbrook = Stop(4, "Morrisons-Gadbrook");
        var stops = new[] { selsey, merston, runcton, gadbrook };
        var completed = new HashSet<Guid> { merston.Id, runcton.Id };

        Assert.Equal(3, RunProgressionFrontier.Sequence(stops, completed));
        Assert.Equal(gadbrook.Id, RunProgressionFrontier.NextOperationalStop(stops, completed)?.Id);
        Assert.Equal(new[] { gadbrook.Id }, RunProgressionFrontier.RemainingOperationalStops(stops, completed).Select(x => x.Id));
        Assert.Equal(new[] { selsey.Id }, RunProgressionFrontier.EvidenceGapsBeforeFrontier(stops, completed).Select(x => x.Id));
        Assert.False(RunProgressionFrontier.FinalStopCompleted(stops, completed));
    }

    [Fact]
    public void Final_stop_departure_completes_run_even_if_earlier_evidence_gap_remains()
    {
        var first = Stop(1, "Collection evidence gap");
        var final = Stop(2, "Final customer");
        var stops = new[] { first, final };
        var completed = new HashSet<Guid> { final.Id };

        Assert.True(RunProgressionFrontier.FinalStopCompleted(stops, completed));
        Assert.Null(RunProgressionFrontier.NextOperationalStop(stops, completed));
        Assert.Equal(first.Id, Assert.Single(RunProgressionFrontier.EvidenceGapsBeforeFrontier(stops, completed)).Id);
    }

    private static LoadStop Stop(int sequence, string name) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = sequence,
        Name = name
    };
}
