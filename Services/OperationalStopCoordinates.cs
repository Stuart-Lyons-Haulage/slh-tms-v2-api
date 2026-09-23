using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Resolves a routable operational coordinate for a planned stop.
/// Canonical Site Master identity and approved/linked geofences are authoritative for
/// operational routing. Coordinates copied onto an imported order are only a fallback;
/// this prevents stale or incorrectly geocoded order coordinates from producing impossible
/// ETAs when a trusted Site Master/geofence location is already known.
/// </summary>
public static class OperationalStopCoordinates
{
    public static (decimal Longitude, decimal Latitude)? Resolve(
        LoadStop stop,
        PlannerSourceMasterDataResolver? masterData = null)
    {
        // 1. Prefer canonical Site Master / linked geofence evidence. Resolve by the
        // planner-facing stop name first and then its physical address. The resolver itself
        // prefers Site Master coordinates, then linked geofence centre, then approved
        // embedded DOT/Falcon fence coordinates.
        if (masterData is not null)
        {
            var resolved = masterData.Resolve(stop.Name);
            if (resolved.Longitude is not null && resolved.Latitude is not null)
                return (resolved.Longitude.Value, resolved.Latitude.Value);

            if (!string.IsNullOrWhiteSpace(stop.Address))
            {
                resolved = masterData.Resolve(stop.Address);
                if (resolved.Longitude is not null && resolved.Latitude is not null)
                    return (resolved.Longitude.Value, resolved.Latitude.Value);
            }
        }

        // 2. Approved embedded geofences are the next authoritative physical-location
        // source. This also covers sites that have not yet been fully enriched in Site Master.
        var canonical = GeofencePlanningMatch.MatchText(stop.Name);
        var exact = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => Normalize(fence.Name) == Normalize(canonical))
            .ToList();
        if (exact.Count == 1)
            return OperationalRunOrigin.FenceCentre(exact[0]);

        var physical = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => GeofencePlanningMatch.SamePhysicalSite(stop, fence))
            .ToList();
        if (physical.Count == 1)
            return OperationalRunOrigin.FenceCentre(physical[0]);

        // 3. Imported/order-level coordinates are deliberately last. They are useful for
        // one-off sites but must never override a known canonical location.
        if (stop.Longitude is not null && stop.Latitude is not null)
            return (stop.Longitude.Value, stop.Latitude.Value);

        return null;
    }

    public static string MissingLocationReason(
        LoadStop stop,
        PlannerSourceMasterDataResolver? masterData = null)
    {
        if (Resolve(stop, masterData) is not null) return string.Empty;

        var hasAddress = !string.IsNullOrWhiteSpace(stop.Address);
        if (!hasAddress)
            return "Physical address/postcode required and no linked geofence could be resolved.";

        return "Physical address/postcode is present but has no routable Site Master/geofence coordinates. Link the approved geofence or geocode the site in Master Data.";
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
}
