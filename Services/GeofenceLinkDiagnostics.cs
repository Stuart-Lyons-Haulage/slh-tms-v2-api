using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class GeofenceLinkDiagnostics
{
    private static readonly HashSet<string> IgnoredNameTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "THE", "LTD", "LIMITED", "PLC", "RDC", "SITE", "DEPOT", "DELIVERY", "COLLECTION", "CUSTOMER"
    };

    public static GeofenceLinkAnalysis Analyze(EmbeddedFence fence, IReadOnlyCollection<Site> sites)
    {
        var distinctSites = sites
            .GroupBy(site => site.Id)
            .Select(group => group.First())
            .ToList();

        var siteNumber = Normalize(fence.SiteNumber);
        if (siteNumber.Length > 0)
        {
            var codeMatches = DistinctById(sites.Where(site => CodesEquivalent(site.ExternalCode, fence.SiteNumber)));
            if (codeMatches.Count == 1)
                return Matched("ExactCode", codeMatches[0], codeMatches);
            if (codeMatches.Count > 1)
                return Ambiguous("AmbiguousCode", codeMatches);
        }

        var fenceName = Normalize(fence.Name);
        var exactNameMatches = DistinctById(sites.Where(site =>
            Normalize(site.Name) == fenceName || Normalize(site.DriverTextName) == fenceName));
        if (exactNameMatches.Count == 1)
            return Matched("ExactNameOrAlias", exactNameMatches[0], exactNameMatches);
        if (exactNameMatches.Count > 1)
            return Ambiguous("AmbiguousExactNameOrAlias", exactNameMatches);

        var fuzzyMatches = DistinctById(sites.Where(site =>
            NamesOverlap(site.Name, fence.Name) || NamesOverlap(site.DriverTextName, fence.Name)));
        if (fuzzyMatches.Count == 1)
            return Matched("UniqueFuzzy", fuzzyMatches[0], fuzzyMatches);
        if (fuzzyMatches.Count > 1)
            return Ambiguous("AmbiguousFuzzy", fuzzyMatches);

        var nearMatches = distinctSites
            .Select(site => new
            {
                Site = site,
                Score = SimilarityScore(fence.Name, site.Name, site.DriverTextName)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Site.Name)
            .Take(5)
            .Select(item => new GeofenceLinkCandidate(item.Site.Id, item.Site.ExternalCode, item.Site.Name, item.Site.DriverTextName, item.Score))
            .ToList();

        return new GeofenceLinkAnalysis(
            "NoCandidate",
            null,
            null,
            false,
            nearMatches.Count,
            nearMatches);
    }

    private static GeofenceLinkAnalysis Matched(string reason, Site site, IReadOnlyCollection<Site> matches) =>
        new(
            reason,
            site.Id,
            site.Name,
            true,
            matches.Count,
            matches.Select(candidate => Candidate(candidate, 100)).ToList());

    private static GeofenceLinkAnalysis Ambiguous(string reason, IReadOnlyCollection<Site> matches) =>
        new(
            reason,
            null,
            null,
            false,
            matches.Count,
            matches.Select(candidate => Candidate(candidate, 100)).ToList());

    private static GeofenceLinkCandidate Candidate(Site site, int score) =>
        new(site.Id, site.ExternalCode, site.Name, site.DriverTextName, score);

    private static List<Site> DistinctById(IEnumerable<Site> sites) =>
        sites.GroupBy(site => site.Id).Select(group => group.First()).ToList();

    private static bool CodesEquivalent(string? siteCode, string? fenceCode)
    {
        var left = Normalize(siteCode);
        var right = Normalize(fenceCode);
        if (left.Length == 0 || right.Length == 0) return false;
        if (left == right) return true;

        return long.TryParse(left, out var leftNumber) &&
               long.TryParse(right, out var rightNumber) &&
               leftNumber == rightNumber;
    }

    private static bool NamesOverlap(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length >= 4 && b.Length >= 4 &&
            (a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)))
            return true;

        var leftTokens = NameTokens(left);
        var rightTokens = NameTokens(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0) return false;
        var common = leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).ToList();
        if (common.Count >= 2) return true;

        var smaller = leftTokens.Count <= rightTokens.Count ? leftTokens : rightTokens;
        return smaller.Count == 1 && common.Count == 1 && common[0].Length >= 7;
    }

    private static int SimilarityScore(string? fenceName, string? siteName, string? driverTextName)
    {
        var fenceTokens = NameTokens(fenceName);
        if (fenceTokens.Count == 0) return 0;

        return Math.Max(TokenScore(fenceTokens, NameTokens(siteName)), TokenScore(fenceTokens, NameTokens(driverTextName)));
    }

    private static int TokenScore(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right)
    {
        if (right.Count == 0) return 0;
        var common = left.Intersect(right, StringComparer.OrdinalIgnoreCase).Count();
        if (common == 0) return 0;
        return (int)Math.Round(100d * common / Math.Max(left.Count, right.Count));
    }

    private static List<string> NameTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var spaced = new string(value.Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ').ToArray());
        return spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2 && !IgnoredNameTokens.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

public sealed record GeofenceLinkAnalysis(
    string Reason,
    Guid? SuggestedSiteId,
    string? SuggestedSiteName,
    bool SafeToAutoLink,
    int CandidateCount,
    IReadOnlyList<GeofenceLinkCandidate> Candidates);

public sealed record GeofenceLinkCandidate(
    Guid SiteId,
    string? ExternalCode,
    string? SiteName,
    string? DriverTextName,
    int Score);
