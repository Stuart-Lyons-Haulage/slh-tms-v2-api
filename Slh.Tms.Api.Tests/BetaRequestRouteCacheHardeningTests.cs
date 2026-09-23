using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class BetaRequestRouteCacheHardeningTests
{
    [Fact]
    public async Task UnavailableRoute_IsEvictedSoSameKeyCanRecoverWithinRequest()
    {
        var cache = new BetaRequestRouteCache();
        var calls = 0;

        var first = await cache.GetOrCreateAsync("route", () =>
        {
            calls++;
            return Task.FromResult<BetaHgvRouteCost?>(null);
        });
        var recovered = new BetaHgvRouteCost(42m, 55, "AzureMapsHgv");
        var second = await cache.GetOrCreateAsync("route", () =>
        {
            calls++;
            return Task.FromResult<BetaHgvRouteCost?>(recovered);
        });

        Assert.Null(first);
        Assert.Same(recovered, second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SuccessfulRoute_IsSharedForRepeatedKey()
    {
        var cache = new BetaRequestRouteCache();
        var calls = 0;
        var expected = new BetaHgvRouteCost(12m, 15, "AzureMapsHgv");

        var first = await cache.GetOrCreateAsync("route", () =>
        {
            calls++;
            return Task.FromResult<BetaHgvRouteCost?>(expected);
        });
        var second = await cache.GetOrCreateAsync("route", () =>
        {
            calls++;
            return Task.FromResult<BetaHgvRouteCost?>(new BetaHgvRouteCost(99m, 99, "unexpected"));
        });

        Assert.Same(expected, first);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
    }
}
