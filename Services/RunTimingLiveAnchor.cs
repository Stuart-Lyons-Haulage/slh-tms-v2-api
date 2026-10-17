using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record RunTimingAnchor(
    (decimal Longitude, decimal Latitude) Origin,
    DateTimeOffset AnchorUtc,
    string Source);

/// <summary>
/// Geofence departure proves journey progression. Fresh RoadTech GPS determines the
/// remaining travel time while the vehicle is moving between stops. If fresh GPS is not
/// available, retain the geofence departure as the resilient fallback anchor.
/// </summary>
public static class RunTimingLiveAnchor
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    public static RunTimingAnchor BetweenStops(
        DateTimeOffset now,
        DateTimeOffset departedAtUtc,
        (decimal Longitude, decimal Latitude) departureOrigin,
        VehicleLiveStatus? live)
    {
        if (live is not null &&
            live.LastReceivedAtUtc <= now.AddMinutes(1) &&
            now - live.LastReceivedAtUtc <= Freshness &&
            live.LastReceivedAtUtc >= departedAtUtc)
        {
            return new RunTimingAnchor(
                (live.Longitude, live.Latitude),
                now,
                "RoadTech live position");
        }

        return new RunTimingAnchor(departureOrigin, departedAtUtc, "Geofence departure fallback");
    }
}
