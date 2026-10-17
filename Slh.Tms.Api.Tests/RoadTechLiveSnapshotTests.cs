using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RoadTechLiveSnapshotTests
{
    [Fact]
    public async Task Successful_refresh_replaces_snapshot_as_one_batch()
    {
        var snapshot = new RoadTechLiveSnapshot();
        var rows = new[]
        {
            new RoadTechTelemetryItem { VehCode = "A" },
            new RoadTechTelemetryItem { VehCode = "B" }
        };

        var refreshed = await snapshot.RefreshAsync(_ => Task.FromResult<IReadOnlyList<RoadTechTelemetryItem>>(rows), CancellationToken.None);
        var read = snapshot.Read();

        Assert.NotNull(refreshed.CapturedAtUtc);
        Assert.Equal(refreshed.CapturedAtUtc, read.CapturedAtUtc);
        Assert.Equal(new[] { "A", "B" }, read.Records.Select(row => row.VehCode));
    }

    [Fact]
    public async Task Failed_refresh_keeps_last_good_snapshot()
    {
        var snapshot = new RoadTechLiveSnapshot();
        await snapshot.RefreshAsync(
            _ => Task.FromResult<IReadOnlyList<RoadTechTelemetryItem>>([new RoadTechTelemetryItem { VehCode = "GOOD" }]),
            CancellationToken.None);
        var before = snapshot.Read();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            snapshot.RefreshAsync(
                _ => throw new InvalidOperationException("provider unavailable"),
                CancellationToken.None));

        var after = snapshot.Read();
        Assert.Equal(before.CapturedAtUtc, after.CapturedAtUtc);
        Assert.Single(after.Records);
        Assert.Equal("GOOD", after.Records[0].VehCode);
    }
}
