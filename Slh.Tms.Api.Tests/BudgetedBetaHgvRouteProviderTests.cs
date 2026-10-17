using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class BudgetedBetaHgvRouteProviderTests
{
    private static readonly BetaRoutePoint[] Points =
    [
        new("A", 50m, -1m),
        new("B", 51m, -1m)
    ];

    [Fact]
    public async Task GetRouteAsync_ReturnsUnavailableWhenWholeRequestBudgetExpires()
    {
        var inner = new SlowRouteProvider(TimeSpan.FromSeconds(5));
        var provider = new BudgetedBetaHgvRouteProvider(
            inner,
            NullLogger<BudgetedBetaHgvRouteProvider>.Instance,
            TimeSpan.FromMilliseconds(75));

        var result = await provider.GetRouteAsync(Points, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task GetRouteAsync_SkipsLaterLookupsAfterBudgetIsExhausted()
    {
        var inner = new SlowRouteProvider(TimeSpan.FromSeconds(5));
        var provider = new BudgetedBetaHgvRouteProvider(
            inner,
            NullLogger<BudgetedBetaHgvRouteProvider>.Instance,
            TimeSpan.FromMilliseconds(50));

        Assert.Null(await provider.GetRouteAsync(Points, CancellationToken.None));
        var timer = Stopwatch.StartNew();
        Assert.Null(await provider.GetRouteAsync(Points, CancellationToken.None));

        // The first lookup proves cancellation/expiry. The second assertion verifies that once
        // the whole-request budget is exhausted we do not call the live provider again. Keep a
        // generous wall-clock guard here because shared GitHub runners can delay timer callbacks
        // under load; the behavioural assertions above are the production contract.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(1, inner.Calls);
    }

    private sealed class SlowRouteProvider(TimeSpan delay) : IBetaHgvRouteProvider
    {
        public int Calls { get; private set; }

        public async Task<BetaHgvRouteCost?> GetRouteAsync(IReadOnlyList<BetaRoutePoint> points, CancellationToken ct)
        {
            Calls++;
            await Task.Delay(delay, ct);
            return new BetaHgvRouteCost(10m, 20, "Test");
        }
    }
}
