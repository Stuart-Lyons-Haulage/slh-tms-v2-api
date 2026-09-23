using Microsoft.Extensions.Options;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class BackloadMatchingServiceTests
{
    [Fact]
    public async Task FindMatchesAsync_FiltersScoresAndReturnsTopThree()
    {
        var routes = new Dictionary<string, RouteMatrixCell?>
        {
            [new MatrixPoint(52.0m, -1.0m).Key] = new(30, 19_312), // 12 miles
            [new MatrixPoint(52.1m, -1.2m).Key] = new(60, 32_187), // 20 miles
            [new MatrixPoint(52.2m, -1.3m).Key] = new(90, 48_280), // 30 miles
            [new MatrixPoint(52.3m, -1.4m).Key] = new(100, 56_327), // 35 miles
            [new MatrixPoint(52.4m, -1.5m).Key] = new(45, 72_000)  // outside 40 miles
        };
        var matrix = new StubMatrixService(routes);
        var service = new BackloadMatchingService(
            matrix,
            Options.Create(new BackloadMatchingOptions()),
            TestTelemetry.Client);
        var current = new MatrixPoint(53.0000m, -1.0000m);
        var candidates = new[]
        {
            Candidate("A", 52.0m, -1.0m, 50.84m, -0.56m, 18),
            Candidate("B", 52.1m, -1.2m, 51.0m, -0.7m, 10),
            Candidate("C", 52.2m, -1.3m, 51.3m, -0.8m, 8),
            Candidate("D", 52.3m, -1.4m, 51.5m, -0.9m, 5),
            Candidate("E", 52.4m, -1.5m, 51.0m, -0.7m, 20),
            // Not southbound by the configured 0.3 degree threshold; never sent to Azure Maps.
            Candidate("NORTH", 52.0m, -1.0m, 51.8m, -1.0m, 20)
        };

        var matches = await service.FindMatchesAsync(current, 20, candidates, new HgvVehicleProfile(), CancellationToken.None);

        Assert.Equal(3, matches.Count);
        Assert.DoesNotContain(matches, item => item.OrderReference is "E" or "NORTH");
        Assert.Equal("A", matches[0].OrderReference);
        Assert.Equal(12.0m, matches[0].EstimatedCollectionDetourMiles);
        Assert.Equal(30, matches[0].EstimatedTimeToCollectionMinutes);
        Assert.Equal(85.0m, matches[0].BackloadScore);
        Assert.Contains("Collection 12mi from current position", matches[0].Reason);
        Assert.Contains("18 pallets", matches[0].Reason);
        Assert.Equal(5, matrix.LastDestinationCount);
    }

    private static BackloadCandidate Candidate(string reference, decimal collectionLat, decimal collectionLon, decimal deliveryLat, decimal deliveryLon, int pallets) =>
        new(Guid.NewGuid(), reference, $"Collection {reference}", new MatrixPoint(collectionLat, collectionLon),
            reference == "A" ? "Barnham" : $"Delivery {reference}", new MatrixPoint(deliveryLat, deliveryLon), pallets);

    private sealed class StubMatrixService(Dictionary<string, RouteMatrixCell?> routes) : IAzureMapsMatrixService
    {
        public int LastDestinationCount { get; private set; }

        public Task<RouteMatrix> ComputeMatrixAsync(IReadOnlyList<MatrixPoint> origins, IReadOnlyList<MatrixPoint> destinations, HgvVehicleProfile profile, CancellationToken ct)
        {
            LastDestinationCount = destinations.Count;
            var row = destinations.Select(destination => routes.GetValueOrDefault(destination.Key)).ToArray();
            return Task.FromResult(new RouteMatrix([row]));
        }

        public Task<RouteMatrixCell?> GetSingleRouteAsync(MatrixPoint origin, MatrixPoint destination, HgvVehicleProfile profile, CancellationToken ct) =>
            Task.FromResult(routes.GetValueOrDefault(destination.Key));
    }
}
