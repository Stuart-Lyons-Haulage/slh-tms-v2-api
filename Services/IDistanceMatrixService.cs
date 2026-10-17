namespace Slh.Tms.Api.Services;

/// <summary>
/// Abstracts the distance matrix calculation service, supporting multiple providers (Azure Maps, Samsara, etc.).
/// </summary>
public interface IDistanceMatrixService
{
    /// <summary>
    /// Computes a matrix of travel distances and times from origins to destinations using the configured vehicle profile.
    /// </summary>
    /// <param name="origins">One or more origin points.</param>
    /// <param name="destinations">One or more destination points.</param>
    /// <param name="profile">Vehicle profile (dimensions, weight, type) that affects routing constraints.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A RouteMatrix with cells containing distance and travel time, or null for unreachable routes.</returns>
    Task<RouteMatrix> ComputeMatrixAsync(
        IReadOnlyList<MatrixPoint> origins,
        IReadOnlyList<MatrixPoint> destinations,
        HgvVehicleProfile profile,
        CancellationToken ct);

    /// <summary>
    /// Convenience method for a single origin-destination route.
    /// </summary>
    Task<RouteMatrixCell?> GetSingleRouteAsync(
        MatrixPoint origin,
        MatrixPoint destination,
        HgvVehicleProfile profile,
        CancellationToken ct);
}
