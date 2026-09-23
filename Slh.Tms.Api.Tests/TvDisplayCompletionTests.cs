using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TvDisplayCompletionTests
{
    [Fact]
    public void Explicitly_completed_run_is_hidden()
    {
        var load = new Load
        {
            Reference = "RUN-COMPLETE",
            Status = LoadStatus.Completed,
            Stops = [new LoadStop { Name = "Final job", Sequence = 1 }]
        };

        Assert.True(TvDisplayController.ShouldHideCompletedRun(load, new HashSet<Guid>()));
    }

    [Fact]
    public void Run_with_every_stop_departed_is_hidden_even_if_status_was_not_updated()
    {
        var first = new LoadStop { Name = "First job", Sequence = 1 };
        var final = new LoadStop { Name = "Final job", Sequence = 2 };
        var load = new Load
        {
            Reference = "RUN-GEOFENCE-COMPLETE",
            Status = LoadStatus.InProgress,
            Stops = [first, final]
        };

        Assert.True(TvDisplayController.ShouldHideCompletedRun(load, new HashSet<Guid> { first.Id, final.Id }));
    }

    [Fact]
    public void Partially_completed_run_remains_visible()
    {
        var first = new LoadStop { Name = "First job", Sequence = 1 };
        var final = new LoadStop { Name = "Final job", Sequence = 2 };
        var load = new Load
        {
            Reference = "RUN-ACTIVE",
            Status = LoadStatus.InProgress,
            Stops = [first, final]
        };

        Assert.False(TvDisplayController.ShouldHideCompletedRun(load, new HashSet<Guid> { first.Id }));
    }
}
