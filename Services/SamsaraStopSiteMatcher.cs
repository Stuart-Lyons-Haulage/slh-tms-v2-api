using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class SamsaraStopSiteMatcher
{
    public static Site? FindSite(LoadStop stop, IReadOnlyList<Site> sites)
    {
        var cleanName = CleanStopName(stop.Name);
        var parts = stop.Name
            .Split(new[] { '·', '•' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (parts.Count > 0 && IsStopAction(parts[0]))
            parts.RemoveAt(0);

        // Compound delivery labels contain the supplier/source followed by the actual
        // physical destination. Prefer the final segment so a shared delivery site is
        // resolved once for every supplier on the route.
        var preferredKey = parts.Count == 0
            ? null
            : Normalise(OperationalStopOrdering.IsCollection(stop.Name) ? parts[0] : parts[^1]);

        // Names and aliases identify the site. Shared supplier addresses must
        // never make a unique name match ambiguous or override a destination.
        if (!string.IsNullOrWhiteSpace(preferredKey))
        {
            var preferred = UniqueMatch(sites, site => IdentityKeys(site).Contains(preferredKey));
            if (preferred.Count > 0) return preferred.Site;
        }

        var nameKey = Normalise(cleanName);
        var named = UniqueMatch(sites, site => IdentityKeys(site).Contains(nameKey));
        if (named.Count > 0) return named.Site;

        var addressKey = Normalise(stop.Address);
        if (addressKey.Length == 0) return null;
        return UniqueMatch(sites, site => Normalise(site.CollectionAddress) == addressKey).Site;
    }

    private static (int Count, Site? Site) UniqueMatch(IReadOnlyList<Site> sites, Func<Site, bool> predicate)
    {
        var matches = sites.Where(predicate).Take(2).ToList();
        return (matches.Count, matches.Count == 1 ? matches[0] : null);
    }

    private static IEnumerable<string> IdentityKeys(Site site) => SiteKeys(site);


    private static IEnumerable<string> SiteKeys(Site site)
    {
        foreach (var value in new[] { site.Name, site.DriverTextName, site.ExternalCode })
        {
            var key = Normalise(value);
            if (key.Length > 0) yield return key;
        }

        foreach (var alias in (site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var key = Normalise(alias);
            if (key.Length > 0) yield return key;
        }
    }

    private static bool IsStopAction(string value) =>
        value.Equals("Collect", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Deliver", StringComparison.OrdinalIgnoreCase);

    private static string CleanStopName(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            value,
            @"^(?:Collect|Deliver)\b\s*(?:[·•:–—-]\s*)?",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
