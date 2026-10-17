using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlanningChangeNotifierTests
{
    [Fact]
    public async Task Publish_ReachesAllActiveSubscribersImmediately()
    {
        var notifier = new PlanningChangeNotifier();
        var first = notifier.Subscribe();
        var second = notifier.Subscribe();

        notifier.Publish("POST /api/v1/planning-control/allocations");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var firstChange = await first.Reader.ReadAsync(timeout.Token);
        var secondChange = await second.Reader.ReadAsync(timeout.Token);

        Assert.Equal("POST /api/v1/planning-control/allocations", firstChange.Reason);
        Assert.Equal(firstChange.Sequence, secondChange.Sequence);
        Assert.True(firstChange.Sequence > 0);

        notifier.Unsubscribe(first.Id);
        notifier.Unsubscribe(second.Id);
    }

    [Fact]
    public void Unsubscribe_CompletesSubscriberWithoutAffectingOthers()
    {
        var notifier = new PlanningChangeNotifier();
        var first = notifier.Subscribe();
        var second = notifier.Subscribe();

        notifier.Unsubscribe(first.Id);
        notifier.Publish("PUT /api/v1/runs/1");

        Assert.True(first.Reader.Completion.IsCompleted);
        Assert.True(second.Reader.TryRead(out var change));
        Assert.Equal("PUT /api/v1/runs/1", change!.Reason);
    }
}
