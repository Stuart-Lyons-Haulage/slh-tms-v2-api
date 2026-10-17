using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RunFinalDestinationTests
{
    [Fact]
    public void Delivery_destination_wins_over_later_operational_stop()
    {
        var deliveryId = Guid.NewGuid();
        var stops = new List<LoadStop>
        {
            new() { Sequence = 1, Name = "Collect · NWF-Selsey" },
            new() { Id = deliveryId, Sequence = 2, Name = "Deliver · Morrisons-Stockton" },
            new() { Sequence = 3, Name = "Return · Lake Lane" }
        };

        var destination = RunFinalDestination.Select(stops);

        Assert.NotNull(destination);
        Assert.Equal(deliveryId, destination!.Id);
        Assert.Equal("Deliver · Morrisons-Stockton", destination.Name);
    }

    [Fact]
    public void Order_linked_stop_is_treated_as_delivery_destination_even_without_prefix()
    {
        var deliveryId = Guid.NewGuid();
        var stops = new List<LoadStop>
        {
            new() { Sequence = 1, Name = "Collection" },
            new() { Id = deliveryId, Sequence = 2, Name = "Morrisons Stockton", OrderId = Guid.NewGuid() },
            new() { Sequence = 3, Name = "Depot return" }
        };

        Assert.Equal(deliveryId, RunFinalDestination.Select(stops)?.Id);
    }

    [Fact]
    public void Last_stop_is_safe_fallback_when_route_has_no_delivery_identity()
    {
        var lastId = Guid.NewGuid();
        var stops = new List<LoadStop>
        {
            new() { Sequence = 1, Name = "Stop A" },
            new() { Id = lastId, Sequence = 2, Name = "Stop B" }
        };

        Assert.Equal(lastId, RunFinalDestination.Select(stops)?.Id);
    }
}
