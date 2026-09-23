using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Resolves the planner's human-readable source-line site labels to stable Site Master
/// identity and the manually linked DOT/Falcon geofence without replacing the source
/// wording used by Planner and driver instructions.
/// </summary>
public sealed class PlannerSourceMasterDataResolver
{
    private static readonly HashSet<string> PlannerPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BAR", "BARFOOTS", "LAN", "LANGMEADS", "SB", "GHS", "SLH", "NWF", "WAITROSE", "MORRISONS", "ALDI"
    };

    private readonly IReadOnlyList<Site> _sites;
    private readonly IReadOnlyList<SiteGeofence> _geofences;
    private readonly IReadOnlyList<MarketContact> _marketContacts;

    private PlannerSourceMasterDataResolver(
        IReadOnlyList<Site> sites,
        IReadOnlyList<SiteGeofence> geofences,
        IReadOnlyList<MarketContact> marketContacts)
    {
        _sites = sites;
        _geofences = geofences;
        _marketContacts = marketContacts;
    }

    public static async Task<PlannerSourceMasterDataResolver> CreateAsync(TmsDbContext db, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        List<SiteGeofence> geofences;
        try
        {
            geofences = await db.SiteGeofences.AsNoTracking().Where(fence => fence.Active).ToListAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            geofences = [];
        }

        List<MarketContact> marketContacts;
        try
        {
            marketContacts = await db.MarketContacts.AsNoTracking().Where(contact => contact.Active).ToListAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            marketContacts = [];
        }

        return new PlannerSourceMasterDataResolver(sites, geofences, marketContacts);
    }

    public PlannerSourceSiteResolution Resolve(string? sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(sourceLabel)) return PlannerSourceSiteResolution.Unresolved(sourceLabel);

        var embeddedFence = ResolveEmbeddedFence(sourceLabel);
        var linkedByFence = embeddedFence is null ? null : LinkedGeofence(embeddedFence.Name);

        var site = MatchSite(sourceLabel)
            ?? SiteFromLink(linkedByFence);

        var linkedGeofence = site is null
            ? linkedByFence
            : _geofences
                .Where(item => item.SiteId == site.Id ||
                    (!string.IsNullOrWhiteSpace(item.SiteNumber) && Normalize(item.SiteNumber) == Normalize(site.ExternalCode)))
                .OrderByDescending(item => item.UpdatedAtUtc)
                .FirstOrDefault()
                ?? linkedByFence;

        embeddedFence ??= ResolveEmbeddedFence(linkedGeofence?.Name)
            ?? ResolveEmbeddedFence(site?.DriverTextName)
            ?? ResolveEmbeddedFence(site?.Name);

        var latitude = site?.Latitude;
        var longitude = site?.Longitude;
        if ((latitude is null || longitude is null) && linkedGeofence is not null)
        {
            var centre = GeofenceCentre(linkedGeofence);
            if (centre is not null)
            {
                longitude = centre.Value.Longitude;
                latitude = centre.Value.Latitude;
            }
        }
        if ((latitude is null || longitude is null) && embeddedFence is not null)
        {
            longitude = (decimal)embeddedFence.Points.Average(point => point.Longitude);
            latitude = (decimal)embeddedFence.Points.Average(point => point.Latitude);
        }

        return new PlannerSourceSiteResolution(
            sourceLabel.Trim(),
            site?.Id,
            site?.ExternalCode,
            site is null ? null : DisplayName(site),
            site?.CollectionAddress,
            latitude,
            longitude,
            linkedGeofence?.Id,
            linkedGeofence?.Name ?? embeddedFence?.Name,
            site is not null,
            linkedGeofence is not null);
    }

    /// <summary>
    /// Returns a canonical Site Master decision for a planned stop versus a physical
    /// embedded geofence. True/false is authoritative when both sides resolve to Site
    /// Master identity. Null means canonical evidence is incomplete and the caller may
    /// use the proven physical/fuzzy matching rules instead of rejecting real telemetry.
    /// </summary>
    public bool? CanonicalGeofenceMatch(string? sourceLabel, EmbeddedFence fence)
    {
        if (string.IsNullOrWhiteSpace(sourceLabel)) return null;

        var linked = LinkedGeofence(fence.Name);
        var linkedSite = SiteFromLink(linked);
        if (linked is null || linkedSite is null) return null;

        var plannedSite = MatchSite(sourceLabel);
        return plannedSite is null ? null : plannedSite.Id == linkedSite.Id;
    }

    private SiteGeofence? LinkedGeofence(string? fenceName)
    {
        if (string.IsNullOrWhiteSpace(fenceName)) return null;
        var key = Normalize(fenceName);
        return _geofences
            .Where(item => Normalize(item.Name) == key || Normalize(item.NormalizedName) == key)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .FirstOrDefault();
    }

    private Site? MatchSite(string value)
    {
        // First honour an exact planner/Site Master identity. This prevents a stripped
        // locality such as "Sittingbourne" from making an exact "Morrisons-Sittingbourne"
        // match look ambiguous when both names exist in Master Data.
        var rawKey = Normalize(value);
        var direct = ExactSites(rawKey);
        if (direct.Count == 1) return direct[0];

        // Market jobs are a two-level master-data relationship: MarketContacts identifies
        // the trader/stall, while Site Master identifies the physical market/geofence.
        // Resolve either a market label (for example COVENTGARDEN) or a unique market
        // customer/stall back to that physical Site before ordinary planner fuzzy matching.
        var marketSite = MatchMarketSite(value);
        if (marketSite is not null) return marketSite;

        var geofenceKey = Normalize(GeofencePlanningMatch.MatchText(value));
        if (geofenceKey.Length > 0 && geofenceKey != rawKey)
        {
            var geofenceExact = ExactSites(geofenceKey);
            if (geofenceExact.Count == 1) return geofenceExact[0];
        }

        var keys = PlannerSiteVariants(value)
            .Select(Normalize)
            .Where(item => item.Length > 0 && item != rawKey && item != geofenceKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var key in keys)
        {
            var exact = ExactSites(key);
            if (exact.Count == 1) return exact[0];
        }

        var fuzzy = _sites.Where(site => SiteCandidates(site).Any(candidate =>
        {
            var candidateKey = Normalize(candidate);
            return candidateKey.Length >= 5 && keys.Any(key => key.Length >= 5 &&
                (key.Contains(candidateKey, StringComparison.Ordinal) || candidateKey.Contains(key, StringComparison.Ordinal)));
        })).ToList();
        return fuzzy.Select(site => site.Id).Distinct().Count() == 1 ? fuzzy[0] : null;
    }

    private Site? MatchMarketSite(string value)
    {
        var marketKey = CanonicalMarket(value);
        if (marketKey is null)
        {
            var sourceKey = Normalize(value);
            var matchingContacts = _marketContacts.Where(contact => MarketEvidence(contact)
                .Any(evidence => Normalize(evidence) == sourceKey)).ToList();
            var markets = matchingContacts
                .Select(contact => CanonicalMarket(contact.Market))
                .Where(key => key is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (markets.Count != 1) return null;
            marketKey = markets[0];
        }

        if (marketKey is null) return null;
        var matches = _sites.Where(site => SiteCandidates(site).Any(candidate =>
        {
            var candidateKey = Normalize(candidate);
            return marketKey switch
            {
                "COVENT" => candidateKey.Contains("COVENTGARDEN", StringComparison.Ordinal),
                "SPIT" => candidateKey.Contains("SPITALFIELDS", StringComparison.Ordinal) || candidateKey.Contains("SPIT", StringComparison.Ordinal),
                "WESTERN" => candidateKey.Contains("WESTERN", StringComparison.Ordinal),
                "SENDER" => candidateKey.Contains("SENDER", StringComparison.Ordinal),
                _ => false
            };
        })).DistinctBy(site => site.Id).ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static IEnumerable<string?> MarketEvidence(MarketContact contact)
    {
        yield return contact.Name;
        yield return contact.StandOrLocation;
        yield return contact.Salesman;
        yield return contact.Sender;
    }

    private static string? CanonicalMarket(string? value)
    {
        var key = Normalize(value);
        if (key.Contains("COVENT", StringComparison.Ordinal)) return "COVENT";
        if (key.Contains("SPITALFIELDS", StringComparison.Ordinal) || key == "SPIT") return "SPIT";
        if (key.Contains("WESTERN", StringComparison.Ordinal)) return "WESTERN";
        if (key.Contains("SENDER", StringComparison.Ordinal)) return "SENDER";
        return null;
    }

    private List<Site> ExactSites(string key) => key.Length == 0
        ? []
        : _sites.Where(site => SiteCandidates(site).Any(candidate => Normalize(candidate) == key)).ToList();

    private static IEnumerable<string> PlannerSiteVariants(string value)
    {
        var initial = value.Trim();
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { initial, GeofencePlanningMatch.MatchText(initial) };

        foreach (var candidate in values.ToList())
        {
            var withoutTemperature = Regex.Replace(candidate, @"\(\s*[+-]?\d+(?:\.\d+)?\s*°?\s*C\s*\)", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(withoutTemperature)) values.Add(withoutTemperature);

            var openParen = candidate.LastIndexOf('(');
            if (openParen > 0 && candidate.EndsWith(')'))
            {
                var before = candidate[..openParen].Trim();
                var inside = candidate[(openParen + 1)..^1].Trim();
                if (!string.IsNullOrWhiteSpace(before)) values.Add(before);
                if (!string.IsNullOrWhiteSpace(inside) && !inside.Contains('°')) values.Add(inside);
            }
        }

        foreach (var candidate in values.ToList())
        {
            var separator = candidate.IndexOf('-');
            if (separator > 0)
            {
                var prefix = candidate[..separator].Trim();
                if (PlannerPrefixes.Contains(prefix)) values.Add(candidate[(separator + 1)..].Trim());
            }
        }

        foreach (var candidate in values.ToList())
        {
            var stripped = Regex.Replace(candidate, @"\s+(CHILL|FRV)$", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(stripped)) values.Add(stripped);
        }

        return values;
    }

    private Site? SiteFromLink(SiteGeofence? linked)
    {
        if (linked is null) return null;
        if (linked.SiteId is Guid siteId)
        {
            var byId = _sites.FirstOrDefault(site => site.Id == siteId);
            if (byId is not null) return byId;
        }
        if (!string.IsNullOrWhiteSpace(linked.SiteNumber))
        {
            var key = Normalize(linked.SiteNumber);
            return _sites.FirstOrDefault(site => Normalize(site.ExternalCode) == key);
        }
        return null;
    }

    private static EmbeddedFence? ResolveEmbeddedFence(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var canonical = GeofencePlanningMatch.MatchText(label);
        var matches = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => Normalize(fence.Name) == Normalize(canonical) || Normalize(fence.Name) == Normalize(label))
            .ToList();
        if (matches.Count == 1) return matches[0];

        var probe = new LoadStop { Name = label.Trim(), Sequence = 1, LoadId = Guid.Empty };
        matches = EmbeddedGeofenceEngine.ApprovedFences.Where(fence => GeofencePlanningMatch.SamePhysicalSite(probe, fence)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static IEnumerable<string?> SiteCandidates(Site site)
    {
        yield return site.ExternalCode;
        yield return site.Name;
        yield return site.DriverTextName;
        foreach (var alias in (site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return alias;
    }

    private static string DisplayName(Site site) => !string.IsNullOrWhiteSpace(site.DriverTextName) ? site.DriverTextName.Trim() : site.Name.Trim();

    private static (decimal Longitude, decimal Latitude)? GeofenceCentre(SiteGeofence geofence)
    {
        if (string.IsNullOrWhiteSpace(geofence.PolygonJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(geofence.PolygonJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var points = new List<(decimal Longitude, decimal Latitude)>();
            foreach (var point in document.RootElement.EnumerateArray())
            {
                if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2 &&
                    point[0].TryGetDecimal(out var longitude) && point[1].TryGetDecimal(out var latitude))
                {
                    points.Add((longitude, latitude));
                    continue;
                }

                if (point.ValueKind != JsonValueKind.Object) continue;
                var objectLongitude = Decimal(point, "longitude") ?? Decimal(point, "lng") ?? Decimal(point, "lon") ?? Decimal(point, "x");
                var objectLatitude = Decimal(point, "latitude") ?? Decimal(point, "lat") ?? Decimal(point, "y");
                if (objectLongitude is not null && objectLatitude is not null)
                    points.Add((objectLongitude.Value, objectLatitude.Value));
            }
            return points.Count == 0
                ? null
                : (points.Average(item => item.Longitude), points.Average(item => item.Latitude));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal? Decimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : null;

    private static string Normalize(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());
}

public sealed record PlannerSourceSiteResolution(
    string? SourceLabel,
    Guid? SiteId,
    string? SiteNumber,
    string? SiteName,
    string? Address,
    decimal? Latitude,
    decimal? Longitude,
    Guid? GeofenceId,
    string? GeofenceName,
    bool SiteMatched,
    bool GeofenceLinked)
{
    public static PlannerSourceSiteResolution Unresolved(string? sourceLabel) =>
        new(sourceLabel, null, null, null, null, null, null, null, null, false, false);

    public string EvidenceNote => string.Join(" · ", new[]
    {
        SiteMatched ? $"Site ref: {SiteNumber}" : "Site ref: unresolved",
        SiteMatched ? $"Master site: {SiteName}" : null,
        GeofenceLinked ? $"Geofence: {GeofenceName}" : "Geofence: unlinked"
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
