using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class PlanningConstraintEvaluator
{
    private static readonly TimeSpan FreshTachoThreshold = TimeSpan.FromHours(6);

    public PlanningConstraintEvaluation EvaluateDriver(PlanningDriverEvidence evidence)
    {
        var results = new List<PlanningConstraintResult>();
        if (evidence.ConsecutiveDays >= 6)
        {
            results.Add(new PlanningConstraintResult(
                "MaximumConsecutiveDays",
                false,
                "Critical",
                "The driver has already worked six consecutive days and cannot be recommended for another duty."));
        }
        else if (evidence.ConsecutiveDays == 5)
        {
            results.Add(new PlanningConstraintResult(
                "AlternatingSixthDay",
                evidence.AlternatingSixthDayAllowed,
                evidence.AlternatingSixthDayAllowed ? "Information" : "Critical",
                evidence.AlternatingSixthDayAllowed
                    ? "A sixth consecutive duty is allowed for this alternating week, subject to current hours evidence."
                    : "A sixth consecutive duty is not allowed for this driver this week."));
        }

        var observedAt = evidence.TachoObservedAtUtc;
        if (observedAt is null)
        {
            results.Add(new PlanningConstraintResult(
                "TachoEvidenceMissing",
                false,
                "Warning",
                "No current TachoMaster hours snapshot is available; planner acknowledgement is required."));
        }
        else if (evidence.EvidenceCapturedAtUtc - observedAt.Value > FreshTachoThreshold || observedAt > evidence.EvidenceCapturedAtUtc.AddMinutes(5))
        {
            results.Add(new PlanningConstraintResult(
                "TachoEvidenceStale",
                false,
                "Warning",
                $"The TachoMaster snapshot is older than {FreshTachoThreshold.TotalHours:0} hours; planner acknowledgement is required."));
        }
        else if (evidence.DriveAvailableTodayMinutes is null)
        {
            results.Add(new PlanningConstraintResult(
                "TachoDriveTimeUnknown",
                false,
                "Warning",
                "TachoMaster is current but did not return remaining daily drive time."));
        }
        else if (evidence.RequiredDriveMinutes > evidence.DriveAvailableTodayMinutes.Value)
        {
            results.Add(new PlanningConstraintResult(
                "InsufficientDriveTime",
                false,
                "Critical",
                $"The run requires {evidence.RequiredDriveMinutes} drive minutes but only {evidence.DriveAvailableTodayMinutes.Value} remain."));
        }
        else
        {
            results.Add(new PlanningConstraintResult(
                "DriveTimeAvailable",
                true,
                "Information",
                $"The run requires {evidence.RequiredDriveMinutes} drive minutes and {evidence.DriveAvailableTodayMinutes.Value} remain."));
        }

        var classification = results.Any(result => !result.Passed && result.Severity == "Critical")
            ? "Blocked"
            : results.Any(result => !result.Passed)
                ? "Unverified"
                : "Recommended";
        return new PlanningConstraintEvaluation(classification, results);
    }
}
