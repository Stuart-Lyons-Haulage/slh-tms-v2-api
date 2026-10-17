using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Compatibility adapter for older planner code paths.
/// Commercial revenue/cost/invoice data is intentionally no longer loaded or saved.
/// Only operational run details, including empty miles, are projected into the active store.
/// </summary>
public static class LoadCommercialStore
{
    public static Task EnrichAsync(TmsDbContext db, IEnumerable<Load> loads, CancellationToken ct)
        => RunOperationalStore.EnrichAsync(db, loads, ct);

    public static Task SaveAsync(TmsDbContext db, Load load, LoadCommercialValues values, string? reviewedBy, CancellationToken ct)
        => RunOperationalStore.SaveAsync(
            db,
            load,
            new RunOperationalValues(
                values.PalletSpacesUsed,
                values.TotalPalletSpaces,
                values.CapacityType,
                values.DepotSplits,
                values.TemperatureC,
                values.PlannerNotes,
                values.EmptyMiles),
            reviewedBy,
            ct);
}

// Kept for compatibility with historic JSON and older call sites while the commercial
// controls are retired. RunOperationalStore only consumes operational fields from it.
public sealed record LoadCommercialValues(decimal? RevenueAmount, decimal? FuelSurchargeAmount, decimal? EstimatedCostAmount, decimal? ActualCostAmount,
    decimal? EstimatedDistanceMiles, decimal? EmptyMiles, string? InvoiceStatus, string? CommercialNotes, decimal? PalletSpacesUsed = null,
    decimal? TotalPalletSpaces = null, string? CapacityType = null, string? DepotSplits = null, decimal? TemperatureC = null, string? PlannerNotes = null);
