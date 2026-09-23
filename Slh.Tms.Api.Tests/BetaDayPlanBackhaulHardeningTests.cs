using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class BetaDayPlanBackhaulHardeningTests
{
    [Fact]
    public async Task ExactSouthboundThreshold_IsEligibleForBackhaulAttachment()
    {
        var builder = new BetaDayPlanBuilder(new FakeRouteProvider());
        var outbound = Movement("NORTH-1", 10,
            new BetaRoutePoint("Selsey", 50.74m, -0.78m),
            new BetaRoutePoint("Darlington", 54.52m, -1.56m));
        var backhaul = Movement("BACK-1", 10,
            new BetaRoutePoint("Bedford", 52.14m, -0.46m),
            new BetaRoutePoint("Bedford South", 52.09m, -0.45m));

        var runs = await builder.BuildAsync(new DateOnly(2026, 9, 9), [outbound, backhaul], CancellationToken.None);

        var run = Assert.Single(runs);
        Assert.Equal(2, run.Orders.Count);
        Assert.Contains(run.Warnings, warning => warning.Contains("Backhaul attached: BACK-1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoCompatibleOutboundRun_LeavesSouthboundMovementStandaloneForPlannerReview()
    {
        var builder = new BetaDayPlanBuilder(new FakeRouteProvider());
        var outbound = Movement("NORTH-1", 10,
            new BetaRoutePoint("Selsey", 50.74m, -0.78m),
            new BetaRoutePoint("Darlington", 54.52m, -1.56m));
        var backhaul = Movement("BACK-1", 10,
            new BetaRoutePoint("Edinburgh", 55.95m, -3.19m),
            new BetaRoutePoint("Chichester", 50.84m, -0.78m));

        var runs = await builder.BuildAsync(new DateOnly(2026, 9, 9), [outbound, backhaul], CancellationToken.None);

        Assert.Equal(2, runs.Count);
        var standalone = Assert.Single(runs.Where(run => run.Orders.Any(order => order.Reference == "BACK-1")));
        Assert.Single(standalone.Orders);
        Assert.Contains(standalone.Warnings, warning => warning.Contains("remains standalone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfiguredSouthboundThreshold_ChangesDirectionalGateWithoutCodeChange()
    {
        var options = new BetaOptimiserOptions { MinSouthboundLatitudeDelta = 0.10m };
        var builder = new BetaDayPlanBuilder(new FakeRouteProvider(), options);
        var outbound = Movement("NORTH-1", 10,
            new BetaRoutePoint("Selsey", 50.74m, -0.78m),
            new BetaRoutePoint("Darlington", 54.52m, -1.56m));
        var shallowSouthbound = Movement("BACK-1", 10,
            new BetaRoutePoint("Bedford", 52.14m, -0.46m),
            new BetaRoutePoint("Bedford South", 52.09m, -0.45m));

        var runs = await builder.BuildAsync(new DateOnly(2026, 9, 9), [outbound, shallowSouthbound], CancellationToken.None);

        Assert.DoesNotContain(runs.SelectMany(run => run.Warnings), warning => warning.Contains("Backhaul attached", StringComparison.OrdinalIgnoreCase));
    }

    private static BetaDayOrderInput Movement(string reference, int pallets, BetaRoutePoint collection, BetaRoutePoint delivery) =>
        new(Guid.NewGuid(), Guid.NewGuid(), reference, "TEST", "AM", "Standard", pallets, new TimeOnly(8, 0), collection, delivery);

    private sealed class FakeRouteProvider : IBetaHgvRouteProvider
    {
        public Task<BetaHgvRouteCost?> GetRouteAsync(IReadOnlyList<BetaRoutePoint> points, CancellationToken ct)
        {
            var miles = Math.Max(points.Count - 1, 0) * 10m;
            return Task.FromResult<BetaHgvRouteCost?>(new BetaHgvRouteCost(miles, (int)miles, "AzureMapsHgv"));
        }
    }
}
