using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// The operational run plan is collection-first: preserve the planner's relative
/// order inside each phase, but do not interleave deliveries between collections.
/// This also provides a compatibility projection for older runs that were saved as
/// Collect/Deliver pairs.
/// </summary>
public static class OperationalStopOrdering
{
    public static int Phase(string? name) => IsDelivery(name) ? 1 : 0;

    public static List<LoadStop> Order(IEnumerable<LoadStop>? stops) => (stops ?? [])
        .Select((stop, index) => new { Stop = stop, Index = index })
        .OrderBy(item => Phase(item.Stop.Name))
        .ThenBy(item => item.Stop.Sequence)
        .ThenBy(item => item.Index)
        .Select(item => item.Stop)
        .ToList();

    public static bool IsCollection(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.TrimStart().StartsWith("Collect", StringComparison.OrdinalIgnoreCase)
            || name.TrimStart().StartsWith("Collection", StringComparison.OrdinalIgnoreCase));

    public static bool IsDelivery(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.TrimStart().StartsWith("Deliver", StringComparison.OrdinalIgnoreCase)
            || name.TrimStart().StartsWith("Delivery", StringComparison.OrdinalIgnoreCase));
}
