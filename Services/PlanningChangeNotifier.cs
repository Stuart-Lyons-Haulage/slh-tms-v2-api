using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Slh.Tms.Api.Services;

public sealed record PlanningDataChanged(long Sequence, DateTimeOffset ChangedAtUtc, string Reason);

public sealed class PlanningChangeNotifier
{
    private readonly ConcurrentDictionary<Guid, Channel<PlanningDataChanged>> subscribers = new();
    private long sequence;

    public (Guid Id, ChannelReader<PlanningDataChanged> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<PlanningDataChanged>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (subscribers.TryRemove(id, out var channel)) channel.Writer.TryComplete();
    }

    public void Publish(string reason)
    {
        var change = new PlanningDataChanged(
            Interlocked.Increment(ref sequence),
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(reason) ? "planning-data-changed" : reason);
        foreach (var subscriber in subscribers.Values) subscriber.Writer.TryWrite(change);
    }
}
