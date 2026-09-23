using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RunCompletionEvidenceTests
{
    [Fact]
    public void In_progress_run_completes_only_when_every_planned_stop_has_confirmed_departure()
    {
        var first = new LoadStop { Id = Guid.NewGuid(), Sequence = 1, Name = "Collection" };
        var second = new LoadStop { Id = Guid.NewGuid(), Sequence = 2, Name = "Delivery" };
        var load = new Load
        {
            Reference = "TEST-RUN-01",
            Status = LoadStatus.InProgress,
            Stops = [first, second]
        };

        Assert.False(RunCompletionEvidence.CanAutoComplete(load, new HashSet<Guid> { first.Id }));
        Assert.True(RunCompletionEvidence.CanAutoComplete(load, new HashSet<Guid> { first.Id, second.Id }));
    }

    [Fact]
    public void Missing_or_unmatched_stop_evidence_never_completes_the_run()
    {
        var first = new LoadStop { Id = Guid.NewGuid(), Sequence = 1, Name = "Collection" };
        var second = new LoadStop { Id = Guid.NewGuid(), Sequence = 2, Name = "Delivery" };
        var load = new Load
        {
            Reference = "TEST-RUN-02",
            Status = LoadStatus.InProgress,
            Stops = [first, second]
        };

        Assert.False(RunCompletionEvidence.CanAutoComplete(load, new HashSet<Guid> { first.Id, Guid.NewGuid() }));
        Assert.False(RunCompletionEvidence.CanAutoComplete(load, new HashSet<Guid>()));
    }

    [Theory]
    [InlineData(LoadStatus.Draft)]
    [InlineData(LoadStatus.Planned)]
    [InlineData(LoadStatus.Dispatched)]
    [InlineData(LoadStatus.Completed)]
    [InlineData(LoadStatus.Cancelled)]
    public void Only_in_progress_runs_can_auto_complete(LoadStatus status)
    {
        var stop = new LoadStop { Id = Guid.NewGuid(), Sequence = 1, Name = "Delivery" };
        var load = new Load { Reference = "TEST-RUN-03", Status = status, Stops = [stop] };

        Assert.False(RunCompletionEvidence.CanAutoComplete(load, new HashSet<Guid> { stop.Id }));
    }

    [Fact]
    public void Empty_or_invalid_stop_plan_never_auto_completes()
    {
        var empty = new Load { Reference = "TEST-RUN-04", Status = LoadStatus.InProgress, Stops = [] };
        var invalid = new Load
        {
            Reference = "TEST-RUN-05",
            Status = LoadStatus.InProgress,
            Stops = [new LoadStop { Id = Guid.Empty, Sequence = 1, Name = "Delivery" }]
        };

        Assert.False(RunCompletionEvidence.CanAutoComplete(empty, new HashSet<Guid>()));
        Assert.False(RunCompletionEvidence.CanAutoComplete(invalid, new HashSet<Guid> { Guid.Empty }));
    }
}
