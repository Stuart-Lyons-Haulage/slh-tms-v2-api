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

    /// <summary>
    /// Projects the common multi-collection pattern into the physical journey
    /// used by Samsara: each collection remains visible, while repeated deliveries
    /// to the same physical destination are represented by one final stop with all
    /// source jobs retained in its notes.
    /// </summary>
    public static List<SamsaraPhysicalStopGroup> GroupCollectionFirst(IEnumerable<SamsaraPhysicalStopCandidate> candidates)
    {
        var ordered = candidates.ToList();
        var collections = GroupAdjacent(ordered.Where(item => item.IsCollection));
        var deliveries = ordered
            .Where(item => !item.IsCollection)
            .GroupBy(Key, StringComparer.Ordinal)
            .Select(group => new SamsaraPhysicalStopGroup(group.ToList()));

        return collections.Concat(deliveries).ToList();
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
