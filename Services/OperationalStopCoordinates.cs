using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Resolves a routable operational coordinate for a planned stop.
/// Site Master physical addresses and coordinates are authoritative for operational routing.
/// </summary>
public static class OperationalStopCoordinates
{
    public static (decimal Longitude, decimal Latitude)? Resolve(
        LoadStop stop,
        PlannerSourceMasterDataResolver? masterData = null)
    {
        // Resolve by the planner-facing stop name and then its physical address.
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

        // Imported/order-level coordinates are only a fallback; they are useful for
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
            return "Physical address/postcode required. Add it to Site Master before dispatch.";

        return "Physical address/postcode is present but has no routable coordinates. Add the coordinates to Site Master.";
    }
}
