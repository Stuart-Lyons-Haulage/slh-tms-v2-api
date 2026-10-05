namespace Slh.Tms.Api.Services;

public sealed record SamsaraPhysicalStopCandidate(
    Guid StopId,
    string Name,
    Guid? SiteId,
    string? Address,
    double? Latitude,
    double? Longitude,
    DateTimeOffset? PlannedArrivalUtc,
    bool IsCollection,
    Guid? OrderId,
    string? PlannerNote);

public sealed record SamsaraPhysicalStopGroup(IReadOnlyList<SamsaraPhysicalStopCandidate> Members)
{
    public SamsaraPhysicalStopCandidate Representative => Members[0];

    public DateTimeOffset? EarliestPlannedArrivalUtc => Members
        .Where(item => item.PlannedArrivalUtc is not null)
        .Select(item => item.PlannedArrivalUtc)
        .OrderBy(item => item)
        .FirstOrDefault();
}

public static class SamsaraPhysicalStopGrouping
{
    public static List<SamsaraPhysicalStopGroup> GroupAdjacent(IEnumerable<SamsaraPhysicalStopCandidate> candidates)
    {
        var groups = new List<SamsaraPhysicalStopGroup>();
        foreach (var candidate in candidates)
        {
            var previous = groups.LastOrDefault();
            if (previous is not null && string.Equals(Key(previous.Representative), Key(candidate), StringComparison.Ordinal))
            {
                groups[^1] = previous with { Members = previous.Members.Append(candidate).ToList() };
                continue;
            }

            groups.Add(new SamsaraPhysicalStopGroup([candidate]));
        }

        return groups;
    }

    private static string Key(SamsaraPhysicalStopCandidate candidate)
    {
        var phase = candidate.IsCollection ? "collection" : "delivery";
        var location = candidate.SiteId is Guid siteId
            ? $"site:{siteId:N}"
            : $"location:{Normalise(candidate.Address)}:{candidate.Latitude:0.00000}:{candidate.Longitude:0.00000}:{Normalise(candidate.Name)}";
        return $"{phase}|{location}";
    }

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
