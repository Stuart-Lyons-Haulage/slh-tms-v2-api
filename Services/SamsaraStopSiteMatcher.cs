using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class SamsaraStopSiteMatcher
{
    public static Site? FindSite(LoadStop stop, IReadOnlyList<Site> sites)
    {
        var fullKeys = new[] { CleanStopName(stop.Name), stop.Address }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalise)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var parts = stop.Name
            .Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (parts.Count > 0 && (parts[0].Equals("Collect", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("Deliver", StringComparison.OrdinalIgnoreCase)))
            parts.RemoveAt(0);

        // Compound delivery labels contain the supplier/source followed by the actual
        // physical destination. Prefer the final segment so a shared delivery site is
        // resolved once for every supplier on the route.
        var preferredKey = parts.Count == 0
            ? null
            : Normalise(OperationalStopOrdering.IsCollection(stop.Name) ? parts[0] : parts[^1]);

        var matches = sites.Where(site =>
        {
            var keys = SiteKeys(site).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (preferredKey is not null && keys.Contains(preferredKey)) || keys.Any(fullKeys.Contains);
        }).ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static IEnumerable<string> SiteKeys(Site site)
    {
        foreach (var value in new[] { site.Name, site.DriverTextName, site.ExternalCode, site.CollectionAddress })
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

    private static string CleanStopName(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"^(?:Collect|Deliver)\s*[·:\-]\s*", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
