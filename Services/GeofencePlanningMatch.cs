using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Bridges planner-facing stop labels to the Falcon geofence naming convention
/// without mutating the operational plan returned to the UI.
/// </summary>
public static class GeofencePlanningMatch
{
    // Falcon history can be sample-sparse at short collection stops. A linked entry and exit
    // at least 30 seconds apart is enough to prove the vehicle visited and left that planned stop;
    // zero-duration/very brief drive-by samples remain unconfirmed unless explicitly confirmed.
    private const int CredibleCompletedVisitSeconds = 30;

    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "NWF", "NATURES", "WAY", "FOODS", "COLLECT", "COLLECTION", "DELIVER", "DELIVERY", "CUSTOMER", "RDC", "SITE", "DEPOT"
    };

    private static readonly string[] OperationalPrefixes =
    [
        "COLLECT", "COLLECTION", "DELIVER", "DELIVERY"
    ];

    public static IReadOnlyList<Load> PrepareLoads(IEnumerable<Load> loads) => loads.Select(PrepareLoad).ToList();

    public static Load PrepareLoad(Load load)
    {
        return new Load
        {
            Id = load.Id,
            Reference = load.Reference,
            PlanningDate = load.PlanningDate,
            Status = load.Status,
            VehicleId = load.VehicleId,
            DriverId = load.DriverId,
            TrailerId = load.TrailerId,
            CreatedAtUtc = load.CreatedAtUtc,
            Stops = (load.Stops ?? []).Select(stop => new LoadStop
            {
                Id = stop.Id,
                LoadId = stop.LoadId,
                OrderId = stop.OrderId,
                Sequence = stop.Sequence,
                Name = MatchStop(stop),
                Address = MatchText(stop.Address),
                Latitude = stop.Latitude,
                Longitude = stop.Longitude,
                PlannedArrivalUtc = stop.PlannedArrivalUtc
            }).ToList()
        };
    }

    /// <summary>
    /// The planner deliberately shows concise labels such as "NWF-Runcton" while
    /// DOT/Falcon uses forms such as "Runcton (Natures Way)". Source-line import adds
    /// execution prefixes such as "Collect · NWF-Selsey"; those prefixes are presentation
    /// semantics, not part of the physical-site identity, and are removed before matching.
    /// For NWF planner labels, the locality is authoritative and the exact uploaded DOT
    /// fence name is returned.
    /// </summary>
    public static string MatchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;
        var siteText = StripOperationalPrefix(value);
        var words = Words(siteText);
        if (words.Count < 2 || !words[0].Equals("NWF", StringComparison.OrdinalIgnoreCase)) return siteText;

        var locality = words.Skip(1).Where(word => !NoiseTokens.Contains(word)).ToList();
        if (locality.Count == 0) return siteText;

        var naturesWayMatches = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => IsNaturesWayFence(fence.Name))
            .Where(fence => ContainsAllWords(fence.Name, locality))
            .ToList();
        if (naturesWayMatches.Count == 1) return naturesWayMatches[0].Name;

        var categoryMatches = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => string.Equals(fence.Category, "NWF Collection", StringComparison.OrdinalIgnoreCase))
            .Where(fence => ContainsAllWords(fence.Name, locality))
            .ToList();
        if (categoryMatches.Count == 1) return categoryMatches[0].Name;

        var estateMatches = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => ContainsAllWords(fence.Name, locality))
            .ToList();
        if (estateMatches.Count == 1) return estateMatches[0].Name;

        return string.Join(' ', locality);
    }

    public static HashSet<Guid> CompletedStopIds(Load load, IEnumerable<DerivedVisit> visits)
    {
        var ordered = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
        var completed = new HashSet<Guid>();

        foreach (var visit in visits.Where(IsCompletedVisit))
        {
            var primaryId = visit.LoadStopId!.Value;
            completed.Add(primaryId);
            var index = ordered.FindIndex(stop => stop.Id == primaryId);
            if (index < 0)
            {
                foreach (var stop in ordered.Where(stop => SamePhysicalSite(stop, visit.Fence)))
                    completed.Add(stop.Id);
                continue;
            }

            for (var i = index - 1; i >= 0 && SamePhysicalSite(ordered[i], visit.Fence); i--)
                completed.Add(ordered[i].Id);
            for (var i = index + 1; i < ordered.Count && SamePhysicalSite(ordered[i], visit.Fence); i++)
                completed.Add(ordered[i].Id);
        }

        return completed;
    }

    public static bool SamePhysicalSite(LoadStop stop, EmbeddedFence fence)
    {
        if (StopInsideFence(stop, fence)) return true;

        if (IsNwfPlannerLabel(stop.Name))
        {
            var plannerLocality = NaturesWayLocalityTokens(StripOperationalPrefix(stop.Name));

            if (IsNaturesWayFence(fence.Name))
            {
                var fenceLocality = NaturesWayLocalityTokens(fence.Name);
                if (plannerLocality.Count > 0 && plannerLocality.SetEquals(fenceLocality)) return true;
            }

            var canonical = MatchText(stop.Name);
            return NormalizeName(canonical) == NormalizeName(fence.Name);
        }

        var left = MeaningfulTokens(MatchText(stop.Name));
        var right = MeaningfulTokens(fence.Name);
        if (left.Count == 0 || right.Count == 0) return false;
        if (left.SetEquals(right)) return true;
        var common = left.Intersect(right, StringComparer.OrdinalIgnoreCase).ToList();
        if (common.Count >= 2) return true;
        return common.Count == 1 && common[0].Length >= 5 && (left.Count == 1 || right.Count == 1);
    }

    public static bool IsLakeLaneFence(EmbeddedFence fence) => IsLakeLaneFence(fence.Name);

    public static bool IsLakeLaneFence(string? name)
    {
        var words = Words(name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return words.Contains("LAKE") && words.Contains("LANE");
    }

    public static DerivedVisit? LatestLakeLaneDeparture(EmbeddedGeofenceSnapshot snapshot, Guid? vehicleId)
    {
        if (vehicleId is null) return null;
        return snapshot.Visits
            .Where(visit => visit.VehicleId == vehicleId.Value && visit.ExitedAtUtc is not null && IsLakeLaneFence(visit.Fence))
            .OrderByDescending(visit => visit.ExitedAtUtc)
            .FirstOrDefault();
    }

    private static bool IsCompletedVisit(DerivedVisit visit)
    {
        if (visit.LoadStopId is null || visit.ExitedAtUtc is null) return false;
        if (visit.ConfirmedAtUtc is not null) return true;

        return visit.ExitedAtUtc.Value - visit.EnteredAtUtc >= TimeSpan.FromSeconds(CredibleCompletedVisitSeconds);
    }

    private static string MatchStop(LoadStop stop)
    {
        // Explicit planner NWF labels are the canonical operational identity. Import/site
        // coordinates are useful routing evidence, but can be stale or temporarily linked
        // to the wrong geofence while Master Data is being repaired. Never let that move an
        // NWF stop to a different physical business/site.
        if (IsNwfPlannerLabel(stop.Name)) return MatchText(stop.Name);

        var coordinateFence = FenceContaining(stop.Latitude, stop.Longitude);
        return coordinateFence?.Name ?? MatchText(stop.Name);
    }

    private static EmbeddedFence? FenceContaining(decimal? latitude, decimal? longitude)
    {
        if (latitude is null || longitude is null) return null;
        return EmbeddedGeofenceEngine.ApprovedFences.FirstOrDefault(fence => Contains(fence.Points, longitude.Value, latitude.Value));
    }

    private static bool StopInsideFence(LoadStop stop, EmbeddedFence fence) =>
        stop.Latitude is not null && stop.Longitude is not null && Contains(fence.Points, stop.Longitude.Value, stop.Latitude.Value);

    private static bool Contains(IReadOnlyList<GeoPoint> points, decimal longitude, decimal latitude)
    {
        var x = (double)longitude;
        var y = (double)latitude;
        var inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var pi = points[i];
            var pj = points[j];
            if (((pi.Latitude > y) != (pj.Latitude > y)) &&
                x < (pj.Longitude - pi.Longitude) * (y - pi.Latitude) / (pj.Latitude - pi.Latitude) + pi.Longitude)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static bool ContainsAllWords(string value, IReadOnlyCollection<string> required)
    {
        var words = Words(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return required.Count > 0 && required.All(words.Contains);
    }

    private static bool IsNwfPlannerLabel(string? value)
    {
        var words = Words(StripOperationalPrefix(value));
        return words.Count >= 2 && words[0].Equals("NWF", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripOperationalPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;
        var trimmed = value.Trim();

        var separator = trimmed.IndexOf('·');
        if (separator > 0)
        {
            var prefix = trimmed[..separator].Trim();
            if (OperationalPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                return trimmed[(separator + 1)..].Trim();
        }

        foreach (var prefix in OperationalPrefixes)
        {
            if (trimmed.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
                return trimmed[(prefix.Length + 1)..].Trim();
        }

        return trimmed;
    }

    private static bool IsNaturesWayFence(string? value)
    {
        var words = Words(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return words.Contains("NATURES") && words.Contains("WAY");
    }

    private static string NormalizeName(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static HashSet<string> NaturesWayLocalityTokens(string? value) =>
        Words(value)
            .Where(token => token.Length >= 2 && !NoiseTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> MeaningfulTokens(string? value) => NaturesWayLocalityTokens(value);

    private static List<string> Words(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var spaced = new string(value.Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ').ToArray());
        return spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
