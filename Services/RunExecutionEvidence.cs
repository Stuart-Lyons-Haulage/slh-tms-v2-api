using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Canonical evidence model carried from driver sign-on and vehicle movement
/// through live ETA calculation to customer-facing exports.
/// </summary>
public sealed record RunExecutionEvidence(
    Guid LoadId,
    string LoadReference,
    DateOnly PlanningDate,
    Guid? DriverId,
    string? DriverName,
    string? VehicleRegistration,
    DateTimeOffset? TachoSignOnUtc,
    string? TachoVehicleCode,
    int? DriveAvailableTodayMinutes,
    DateTimeOffset? FirstMovementUtc,
    DateTimeOffset? LatestTrackingUtc,
    string TrackingState,
    string EvidenceStatus,
    string EvidenceExplanation);

public static class RunExecutionEvidenceRules
{
    // RoadTech/DOT is polled every minute. Customer-facing ETAs must therefore
    // stop being treated as live promptly if tracking stops updating.
    public static readonly TimeSpan MaximumLiveTrackingAge = TimeSpan.FromMinutes(5);

    public static string EvidenceStatus(
        TachoVehicleDriverStatus? tacho,
        DateTimeOffset? latestTrackingUtc,
        DateTimeOffset now)
    {
        if (tacho is null && latestTrackingUtc is null) return "Unverified";
        if (tacho is null) return "TrackingOnly";
        if (latestTrackingUtc is null) return tacho.EvidenceSource == "FalconLiveCard" ? "CardOnly" : "TachoOnly";
        return now - latestTrackingUtc <= MaximumLiveTrackingAge ? "VerifiedLive" : "TrackingStale";
    }

    public static string Explanation(
        TachoVehicleDriverStatus? tacho,
        DateTimeOffset? firstMovementUtc,
        DateTimeOffset? latestTrackingUtc,
        DateTimeOffset now)
    {
        if (tacho is null && latestTrackingUtc is null)
            return "No matched TachoMaster duty, Falcon live-card identity or DOT/Falcon tracking evidence is available for this run.";
        if (tacho is null)
            return "DOT/Falcon tracking is available, but no current TachoMaster duty or Falcon live-card identity was matched to the allocated vehicle.";

        var identitySource = tacho.EvidenceSource == "FalconLiveCard"
            ? $"Falcon live card observed for {tacho.DriverName} at {tacho.DutyStartUtc:O}"
            : $"TachoMaster duty sign-on for {tacho.DriverName} at {tacho.DutyStartUtc:O}";
        if (latestTrackingUtc is null)
            return $"{identitySource}, but no DOT/Falcon movement has been matched to the allocated vehicle.";

        var freshness = now - latestTrackingUtc <= MaximumLiveTrackingAge ? "fresh" : "stale";
        var movement = firstMovementUtc is null ? "No movement event has been recorded yet." : $"First vehicle movement was recorded at {firstMovementUtc:O}.";
        return $"{identitySource}; DOT/Falcon tracking is {freshness}. {movement}";
    }
}
