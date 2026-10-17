using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class LiveEtaEligibility
{
    public static bool CanRoute(
        (decimal Longitude, decimal Latitude)? current,
        LoadStop stop,
        DateTimeOffset? trackingObservedAtUtc,
        DateTimeOffset now) =>
        current is not null &&
        stop.Longitude is not null &&
        stop.Latitude is not null &&
        trackingObservedAtUtc is not null &&
        now - trackingObservedAtUtc.Value <= RunExecutionEvidenceRules.MaximumLiveTrackingAge;
}
