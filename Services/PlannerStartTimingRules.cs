using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class PlannerStartTimingRules
{
    public static DateTimeOffset? PreviousRunFinish(
        Load? previousRun,
        IReadOnlyList<SiteTimingRule> timingRules,
        IReadOnlyList<Site> sites)
    {
        if (previousRun is null) return null;

        var plannedFinish = previousRun.Stops
            .Where(stop => stop.PlannedArrivalUtc is not null)
            .Select(stop => stop.PlannedArrivalUtc)
            .Max();

        var masterDeadlines = previousRun.Stops
            .Where(stop => !OperationalStopOrdering.IsCollection(stop.Name))
            .Select(stop => SiteTimingRuleMatcher.MatchForLoad(previousRun, stop, timingRules, sites))
            .Where(rule => rule is not null)
            .Select(rule => SiteTimingRuleMatcher.DeliveryWindow(rule!, previousRun.PlanningDate).End)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToList();

        var masterFinish = masterDeadlines.Count == 0 ? (DateTimeOffset?)null : masterDeadlines.Max();
        return Max(plannedFinish, masterFinish);
    }

    public static DateTimeOffset? LatestCollection(
        Load load,
        LoadStop? firstCollection,
        IReadOnlyList<SiteTimingRule> timingRules,
        IReadOnlyList<Site> sites)
    {
        if (firstCollection is null) return null;
        var delivery = load.Stops
            .OrderBy(stop => stop.Sequence)
            .FirstOrDefault(stop => !OperationalStopOrdering.IsCollection(stop.Name));
        if (delivery is null) return null;

        var rule = SiteTimingRuleMatcher.MatchForLoad(load, delivery, timingRules, sites);
        return rule is null
            ? null
            : SiteTimingRuleMatcher.CollectionWindow(rule, load.PlanningDate).End;
    }

    public static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset? right) =>
        right is DateTimeOffset value && value > left ? value : left;

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left.Value >= right.Value ? left : right;
    }
}
