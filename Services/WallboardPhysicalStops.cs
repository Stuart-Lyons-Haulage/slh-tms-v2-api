using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Builds the execution journey used by route progress views without changing the
/// underlying order lines. Consecutive rows for the same action at the same physical
/// site represent one vehicle visit, even when several orders are handled there.
///
/// Physical identity uses mapped coordinates/address and falls back to the display
/// label. This prevents several orders for one real visit (for example repeated Selsey,
/// Runcton or Morrisons Stockton lines) from inflating progress or ETA routing.
/// </summary>
public static class WallboardPhysicalStops
{
    public static IReadOnlyList<LoadStop> Collapse(IEnumerable<LoadStop>? stops)
    {
        var result = new List<LoadStop>();
        string? previousKey = null;

        foreach (var source in (stops ?? []).OrderBy(stop => stop.Sequence))
        {
            var key = PhysicalVisitKey(source);
            if (result.Count > 0 && string.Equals(key, previousKey, StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(new LoadStop
            {
                Id = source.Id,
                LoadId = source.LoadId,
                OrderId = source.OrderId,
                Sequence = result.Count + 1,
                Name = source.Name,
                Address = source.Address,
                Latitude = source.Latitude,
                Longitude = source.Longitude,
                PlannedArrivalUtc = source.PlannedArrivalUtc,
                PlannerNote = source.PlannerNote
            });
            previousKey = key;
        }

        return result;
    }

    public static void Apply(IEnumerable<Load> loads)
    {
        foreach (var load in loads) load.Stops = Collapse(load.Stops).ToList();
    }

    private static string PhysicalVisitKey(LoadStop stop)
    {
        var action = Action(stop.Name);

        if (stop.Latitude is decimal latitude && stop.Longitude is decimal longitude)
        {
            // Four decimal places is roughly an 11m grid in the UK: tight enough to keep
            // neighbouring premises separate while making duplicate order-line coordinates
            // unequivocally the same vehicle visit.
            return $"{action}:COORD:{Math.Round(latitude, 4):F4}:{Math.Round(longitude, 4):F4}";
        }

        var address = Normalize(StripOperationalPrefix(stop.Address));
        if (address.Length >= 5) return $"{action}:ADDRESS:{address}";

        return $"{action}:NAME:{Normalize(StripOperationalPrefix(stop.Name))}";
    }

    private static string StripOperationalPrefix(string? value) =>
        System.Text.RegularExpressions.Regex.Replace(value?.Trim() ?? string.Empty, @"^(COLLECT|COLLECTION|DELIVER|DELIVERY)\s*[·:\-]\s*", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    private static string Action(string? name)
    {
        var value = name?.Trim() ?? string.Empty;
        if (value.StartsWith("Deliver", StringComparison.OrdinalIgnoreCase)) return "D";
        if (value.StartsWith("Collect", StringComparison.OrdinalIgnoreCase)) return "C";
        return "S";
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
