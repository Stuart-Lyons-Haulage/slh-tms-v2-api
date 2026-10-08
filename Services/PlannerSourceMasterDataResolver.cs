using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Resolves the planner's human-readable source-line site labels to stable Site Master
/// identity without replacing the source wording used by Planner and driver instructions.
/// </summary>
public sealed class PlannerSourceMasterDataResolver
{
    private static readonly HashSet<string> PlannerPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BAR", "BARFOOTS", "LAN", "LANGMEADS", "SB", "GHS", "SLH", "NWF", "WAITROSE", "MORRISONS", "ALDI"
    };

    private readonly IReadOnlyList<Site> _sites;
    private readonly IReadOnlyList<MarketContact> _marketContacts;

    private PlannerSourceMasterDataResolver(
        IReadOnlyList<Site> sites,
        IReadOnlyList<MarketContact> marketContacts)
    {
        _sites = sites;
        _marketContacts = marketContacts;
    }

    public static async Task<PlannerSourceMasterDataResolver> CreateAsync(TmsDbContext db, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

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

        return new PlannerSourceMasterDataResolver(sites, marketContacts);
    }

    public PlannerSourceSiteResolution Resolve(string? sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(sourceLabel)) return PlannerSourceSiteResolution.Unresolved(sourceLabel);

        var site = MatchSite(sourceLabel);
        var latitude = site?.Latitude;
        var longitude = site?.Longitude;

        return new PlannerSourceSiteResolution(
            sourceLabel.Trim(),
            site?.Id,
            site?.ExternalCode,
            site is null ? null : DisplayName(site),
            site?.CollectionAddress,
            latitude,
            longitude,
            site is not null);
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
        // the trader/stall, while Site Master identifies the physical market.
        // Resolve either a market label (for example COVENTGARDEN) or a unique market
        // customer/stall back to that physical Site before ordinary planner fuzzy matching.
        var marketSite = MatchMarketSite(value);
        if (marketSite is not null) return marketSite;

        var keys = PlannerSiteVariants(value)
            .Select(Normalize)
            .Where(item => item.Length > 0 && item != rawKey)
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
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { initial, StripOperationalPrefix(initial) };

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

    private static string StripOperationalPrefix(string value) =>
        Regex.Replace(value.Trim(), @"^(COLLECT|COLLECTION|DELIVER|DELIVERY)\s*[·:\-]\s*", string.Empty, RegexOptions.IgnoreCase).Trim();

    private static IEnumerable<string?> SiteCandidates(Site site)
    {
        yield return site.ExternalCode;
        yield return site.Name;
        yield return site.DriverTextName;
        foreach (var alias in (site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return alias;
    }

    private static string DisplayName(Site site) => !string.IsNullOrWhiteSpace(site.DriverTextName) ? site.DriverTextName.Trim() : site.Name.Trim();

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
    bool SiteMatched)
{
    public static PlannerSourceSiteResolution Unresolved(string? sourceLabel) =>
        new(sourceLabel, null, null, null, null, null, null, false);

    public string EvidenceNote => string.Join(" · ", new[]
    {
        SiteMatched ? $"Site ref: {SiteNumber}" : "Site ref: unresolved",
        SiteMatched ? $"Master site: {SiteName}" : null
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
