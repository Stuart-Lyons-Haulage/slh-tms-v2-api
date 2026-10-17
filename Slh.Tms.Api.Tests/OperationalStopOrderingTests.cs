using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationalStopOrderingTests
{
    [Fact]
    public void Alternating_planner_pairs_are_projected_as_all_collections_then_all_deliveries()
    {
        var stops = new[]
        {
            Stop(1, "Collect · Greenhouse"),
            Stop(2, "Deliver · DARLINGTON"),
            Stop(3, "Collect · Selsey"),
            Stop(4, "Deliver · Aldi DARLINGTON Distribution Centre"),
            Stop(5, "Collect · Selsey"),
            Stop(6, "Deliver · Morrisons Stockton"),
            Stop(7, "Collect · NWF - Runcton"),
            Stop(8, "Deliver · Morrisons Stockton")
        };

        var ordered = OperationalStopOrdering.Order(stops);

        Assert.Equal(
            new[]
            {
                "Collect · Greenhouse",
                "Collect · Selsey",
                "Collect · Selsey",
                "Collect · NWF - Runcton",
                "Deliver · DARLINGTON",
                "Deliver · Aldi DARLINGTON Distribution Centre",
                "Deliver · Morrisons Stockton",
                "Deliver · Morrisons Stockton"
            },
            ordered.Select(stop => stop.Name));
    }

    [Fact]
    public void Relative_order_inside_collection_and_delivery_phases_is_preserved()
    {
        var stops = new[]
        {
            Stop(9, "Deliver · C"),
            Stop(4, "Collect · B"),
            Stop(2, "Collect · A"),
            Stop(7, "Deliver · B")
        };

        var ordered = OperationalStopOrdering.Order(stops);

        Assert.Equal(new[] { "Collect · A", "Collect · B", "Deliver · B", "Deliver · C" }, ordered.Select(stop => stop.Name));
    }

    private static LoadStop Stop(int sequence, string name) => new()
    {
        Id = Guid.NewGuid(),
        LoadId = Guid.NewGuid(),
        Sequence = sequence,
        Name = name
    };
}
