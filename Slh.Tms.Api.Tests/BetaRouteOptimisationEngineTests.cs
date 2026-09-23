using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class BetaRouteOptimisationEngineTests
{
    [Fact]
    public async Task AnalyseAsync_ChoosesLiveAzureHgvSavingAndKeepsEvidence()
    {
        var provider = new FakeRouteProvider(new Dictionary<string, BetaHgvRouteCost>
        {
            ["A>B>C"] = new(100m, 150, "AzureMapsHgv"),
            ["B>A>C"] = new(108m, 165, "AzureMapsHgv"),
            ["A>C>B"] = new(72m, 112, "AzureMapsHgv")
        });
        var engine = new BetaRouteOptimisationEngine(provider);
        var input = new BetaRouteInput(
            Guid.NewGuid(),
            "RUN 1 AM",
            false,
            [
                Stop("A"),
                Stop("B"),
                Stop("C")
            ]);

        var result = await engine.AnalyseAsync(input, CancellationToken.None);

        Assert.True(result.RoutingAvailable);
        Assert.Equal("AzureMapsHgv", result.RoutingSource);
        Assert.Equal(100m, result.CurrentMiles);
        Assert.Equal(72m, result.ProposedMiles);
        Assert.Equal(28m, result.SavingMiles);
        Assert.Equal(new[] { "A", "C", "B" }, result.After.Select(stop => stop.Name));
        Assert.Contains("Azure Maps", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyseAsync_DoesNotOptimiseWhenAzureHgvEvidenceIsUnavailable()
    {
        var engine = new BetaRouteOptimisationEngine(new FakeRouteProvider(new Dictionary<string, BetaHgvRouteCost>()));
        var input = new BetaRouteInput(Guid.NewGuid(), "RUN 2 PM", false, [Stop("A"), Stop("B"), Stop("C")]);

        var result = await engine.AnalyseAsync(input, CancellationToken.None);

        Assert.False(result.RoutingAvailable);
        Assert.Null(result.ProposedMiles);
        Assert.Null(result.SavingMiles);
        Assert.Equal(result.Before, result.After);
        Assert.Contains("Azure Maps HGV routing", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyseAsync_DoesNotReverseStopsBelongingToTheSameOrder()
    {
        var orderId = Guid.NewGuid();
        var provider = new FakeRouteProvider(new Dictionary<string, BetaHgvRouteCost>
        {
            ["COLLECT>DELIVER>X"] = new(90m, 130, "AzureMapsHgv"),
            ["DELIVER>COLLECT>X"] = new(40m, 70, "AzureMapsHgv"),
            ["COLLECT>X>DELIVER"] = new(92m, 134, "AzureMapsHgv")
        });
        var engine = new BetaRouteOptimisationEngine(provider);
        var input = new BetaRouteInput(
            Guid.NewGuid(),
            "RUN 3 AM",
            false,
            [
                Stop("COLLECT", orderId),
                Stop("DELIVER", orderId),
                Stop("X")
            ]);

        var result = await engine.AnalyseAsync(input, CancellationToken.None);

        Assert.True(result.RoutingAvailable);
        Assert.Null(result.ProposedMiles);
        Assert.Equal(new[] { "COLLECT", "DELIVER", "X" }, result.After.Select(stop => stop.Name));
    }

    private static BetaRouteInputStop Stop(string name, Guid? orderId = null) =>
        new(Guid.NewGuid(), orderId, name, 50m, -1m);

    private sealed class FakeRouteProvider(IReadOnlyDictionary<string, BetaHgvRouteCost> costs) : IBetaHgvRouteProvider
    {
        public Task<BetaHgvRouteCost?> GetRouteAsync(IReadOnlyList<BetaRoutePoint> points, CancellationToken ct)
        {
            var key = string.Join('>', points.Select(point => point.Name));
            return Task.FromResult(costs.TryGetValue(key, out var cost) ? cost : null);
        }
    }
}
