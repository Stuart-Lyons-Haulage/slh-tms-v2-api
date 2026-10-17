using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class PlanningCandidateRanker
{
    private static readonly TimeSpan FreshTrackingThreshold = TimeSpan.FromHours(6);

    public PlanningCandidateScore Score(PlanningCandidateEvidence evidence)
    {
        var (positionSource, startLatitude) = StartPosition(evidence);
        var components = new List<PlanningScoreComponent>();
        var explanations = new List<string>();

        var utilisation = Math.Round(Math.Clamp(evidence.UtilisationPercent, 0m, 100m) * 0.5m, 2);
        components.Add(new PlanningScoreComponent(
            "Utilisation",
            utilisation,
            $"{Math.Clamp(evidence.UtilisationPercent, 0m, 100m):0.#}% trailer utilisation contributes {utilisation:0.##} points."));

        if (startLatitude is decimal start && evidence.CollectionLatitude is decimal collection)
        {
            var repositionMiles = Math.Abs(start - collection) * 69m;
            var reposition = -Math.Min(25m, Math.Round(repositionMiles / 4m, 2));
            components.Add(new PlanningScoreComponent(
                "RepositionDistance",
                reposition,
                $"{positionSource} gives an approximate {repositionMiles:0.#}-mile north/south reposition to collection."));
            explanations.Add($"Start position uses {PositionLabel(positionSource)} before the collection ranking is calculated.");
        }
        else
        {
            components.Add(new PlanningScoreComponent(
                "PositionEvidence",
                -10m,
                "No current or previous end latitude is available, so positioning remains unverified."));
            explanations.Add("No reliable start position is available; planner review is required.");
        }

        if (startLatitude is decimal north && north >= 53m && evidence.DeliveryLatitude is decimal delivery && delivery <= 52.5m)
        {
            components.Add(new PlanningScoreComponent(
                "SouthboundPositioning",
                15m,
                "The vehicle/driver starts north and this work moves south toward the delivery area."));
            explanations.Add("Southbound work is prioritised because the previous/live position is north of the delivery area.");
        }

        if (evidence.ConsecutiveDays >= 5 && evidence.DeliveryLatitude is decimal homeward && homeward <= 52.5m)
        {
            components.Add(new PlanningScoreComponent(
                "ReturnHome",
                20m,
                $"After {evidence.ConsecutiveDays} consecutive days, the southern delivery supports return-home planning."));
            explanations.Add($"Return-home preference is active after {evidence.ConsecutiveDays} consecutive days away.");
        }

        return new PlanningCandidateScore(
            Math.Round(components.Sum(component => component.Value), 2),
            positionSource,
            startLatitude,
            components,
            explanations);
    }

    private static (string Source, decimal? Latitude) StartPosition(PlanningCandidateEvidence evidence)
    {
        if (evidence.LiveLatitude is decimal live &&
            evidence.LiveObservedAtUtc is DateTimeOffset liveAt &&
            liveAt <= evidence.EvidenceCapturedAtUtc.AddMinutes(5) &&
            evidence.EvidenceCapturedAtUtc - liveAt <= FreshTrackingThreshold)
            return ("LiveTracking", live);

        if (evidence.PreviousEndLatitude is decimal previous)
            return ("PreviousRunEnd", previous);

        return ("Unavailable", null);
    }

    private static string PositionLabel(string source) => source switch
    {
        "LiveTracking" => "fresh live tracking",
        "PreviousRunEnd" => "the previous run end",
        _ => "unavailable evidence"
    };
}
