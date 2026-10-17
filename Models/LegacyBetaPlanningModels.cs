namespace Slh.Tms.Api.Models;

/// <summary>
/// Legacy Beta planning models - kept for backward compatibility during migration.
/// These types are no longer actively used in the TMS V2 system but are referenced
/// by archived services and should be removed once all references are cleaned up.
/// </summary>

/// <summary>
/// A single order in a day plan (legacy beta system).
/// </summary>
public sealed record BetaDayOrderInput(
    Guid OrderId,
    string OrderReference,
    string CustomerName,
    int PalletCount,
    decimal TonnageWeight,
    string DeliveryDestination,
    decimal? DeliveryLatitude,
    decimal? DeliveryLongitude,
    TimeSpan? EarliestCollectionTime = null,
    TimeSpan? LatestCollectionTime = null,
    TimeSpan? EarliestDeliveryTime = null,
    TimeSpan? LatestDeliveryTime = null);

/// <summary>
/// Route cost estimation for HGV journeys (legacy beta system).
/// </summary>
public sealed record BetaHgvRouteCost(
    decimal DistanceMiles,
    decimal FuelCostGbp,
    decimal DrivingTimeHours,
    decimal CO2TonnesPerTonne,
    string RouteId,
    bool? IsTollRoute = null);

/// <summary>
/// Interface for the legacy beta HGV route provider (no longer used).
/// Kept for type resolution only - implementation is stubbed.
/// </summary>
public interface IBetaHgvRouteProvider
{
    /// <summary>Not implemented - this interface exists for backward compatibility only.</summary>
    Task<BetaHgvRouteCost?> EstimateCostAsync(decimal originLat, decimal originLng, decimal destLat, decimal destLng, CancellationToken ct);
}

/// <summary>
/// Legacy beta plan builder (no longer used).
/// This was replaced by RunBuilder and order status transitions.
/// Kept as a stub to resolve compilation errors.
/// </summary>
public sealed class BetaDayPlanBuilder
{
    private readonly List<BetaDayOrderInput> _orders = [];

    /// <summary>Not implemented - this class exists for backward compatibility only.</summary>
    public void AddOrder(BetaDayOrderInput order) => _orders.Add(order);

    /// <summary>Not implemented - this class exists for backward compatibility only.</summary>
    public IReadOnlyList<BetaDayOrderInput> Orders => _orders.AsReadOnly();
}

/// <summary>
/// Legacy stub service for referencing missing types.
/// This service should not be instantiated; it exists only to resolve
/// compilation errors during the migration from beta to production planning.
/// </summary>
public sealed class LegacyBetaPlanningStub : IBetaHgvRouteProvider
{
    public Task<BetaHgvRouteCost?> EstimateCostAsync(decimal originLat, decimal originLng, decimal destLat, decimal destLng, CancellationToken ct)
    {
        throw new NotImplementedException("Legacy beta planning is no longer supported. Use the production planning system instead.");
    }
}
