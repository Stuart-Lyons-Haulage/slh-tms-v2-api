using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Selects the customer-facing final delivery destination for a run. Operational routes
/// can contain work after the last customer delivery, so the highest stop sequence is not
/// always the destination that the wallboard ETA should promise.
/// </summary>
public static class RunFinalDestination
{
    public static LoadStop? Select(IEnumerable<LoadStop>? stops)
    {
        var ordered = (stops ?? []).OrderBy(stop => stop.Sequence).ToList();
        if (ordered.Count == 0) return null;
        return ordered.LastOrDefault(IsDeliveryDestination) ?? ordered[^1];
    }

    public static bool IsDeliveryDestination(LoadStop stop) =>
        stop.OrderId is not null || stop.Name.StartsWith("Deliver", StringComparison.OrdinalIgnoreCase);
}
