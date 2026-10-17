using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Process-wide immutable snapshot of the most recent successful RoadTech Falcon live read.
/// Only the tracking ingestion worker refreshes this snapshot. All request/diagnostic paths
/// read the same captured batch so wallboards, ETA, dispatch and compliance enrichment cannot
/// independently fan out to RoadTech or observe different provider read times.
/// </summary>
public sealed class RoadTechLiveSnapshot
{
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private RoadTechLiveSnapshotState state = new([], null);

    public RoadTechLiveSnapshotState Read() => Volatile.Read(ref state);

    public async Task<RoadTechLiveSnapshotState> RefreshAsync(
        Func<CancellationToken, Task<IReadOnlyList<RoadTechTelemetryItem>>> fetch,
        CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var records = (await fetch(cancellationToken)).ToArray();
            var refreshed = new RoadTechLiveSnapshotState(records, DateTimeOffset.UtcNow);
            Volatile.Write(ref state, refreshed);
            return refreshed;
        }
        finally
        {
            refreshGate.Release();
        }
    }
}

public sealed record RoadTechLiveSnapshotState(
    IReadOnlyList<RoadTechTelemetryItem> Records,
    DateTimeOffset? CapturedAtUtc);
