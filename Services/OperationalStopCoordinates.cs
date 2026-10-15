using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Resolves a routable operational coordinate for a planned stop.
/// Canonical Site Master identity and physical coordinates are authoritative for
/// operational routing. Samsara owns live route/geofence execution; the TMS only
/// supplies the physical stop address and coordinates needed to build the route.
/// </summary>
public static class OperationalStopCoordinates
{
    public static (decimal Longitude, decimal Latitude)? Resolve(
        LoadStop stop,
        PlannerSourceMasterDataResolver? masterData = null)
    {
        // 1. Prefer the canonical Site Master coordinates. Resolve by the planner-facing
        // stop name first and then its physical address. Geofence coordinates are never
        // used as a routing fallback.
        if (masterData is not null)
        {
            var resolved = masterData.ResolveSiteMasterCoordinates(stop.Name);
            if (resolved is not null) return resolved;

            if (!string.IsNullOrWhiteSpace(stop.Address))
            {
                resolved = masterData.ResolveSiteMasterCoordinates(stop.Address);
                if (resolved is not null) return resolved;
            }
        }

        // 2. Imported/order-level coordinates are only a temporary fallback for legacy
        // rows. New and corrected sites must be fixed in Site Master.
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
            return "Physical address/postcode required in Site Master.";

        return "Physical address/postcode is present but Site Master has no latitude/longitude. Correct the site in Master Data.";
    }
}
