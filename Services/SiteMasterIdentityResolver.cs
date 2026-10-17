using System.Text.Json;
using System.Text.RegularExpressions;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record IncomingSiteIdentity(
    string? ExternalCode,
    string? Name,
    string? DriverTextName,
    string? CollectionAddress,
    string? Aliases,
    string? MapLink);

public sealed record SiteIdentityResolution(
    Site? Site,
    string Outcome,
    int Confidence,
    string Reason,
    bool CanCreate,
    bool RequiresReview,
    IReadOnlyList<Site> PossibleDuplicates)
{
    public bool Matched => Site is not null;
}

public static class SiteMasterIdentityResolver
{
    public static SiteIdentityResolution Resolve(IncomingSiteIdentity incoming, IEnumerable<Site> liveSites)
    {
        var sites = liveSites.Where(site => site.Active).ToList();
        var code = Normalise(incoming.ExternalCode);
        var name = Normalise(incoming.Name);
        var driverName = Normalise(incoming.DriverTextName);
        var address = NormaliseAddress(incoming.CollectionAddress);
        var postcode = ExtractPostcode(incoming.CollectionAddress);
        var map = NormaliseMap(incoming.MapLink);
        var aliases = SplitAliases(incoming.Aliases).ToList();
        var incomingAliasCandidates = new[] { incoming.Name, incoming.DriverTextName }
            .Concat(aliases)
            .Select(Normalise)
            .Where(value => value.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (code.Length > 0)
        {
            var byCode = sites.FirstOrDefault(site => Normalise(site.ExternalCode) == code);
            if (byCode is not null)
                return new SiteIdentityResolution(byCode, "matched", 100, "Matched existing live site by SiteID/ExternalCode.", false, false, []);
        }

        var strong = new List<(Site Site, int Confidence, string Reason)>();
        foreach (var site in sites)
        {
            var siteName = Normalise(site.Name);
            var siteDriverName = Normalise(site.DriverTextName);
            var siteAddress = NormaliseAddress(site.CollectionAddress);
            var sitePostcode = ExtractPostcode(site.CollectionAddress);
            var siteMap = NormaliseMap(site.MapLink);
            var siteAliases = SplitAliases(site.Aliases).Select(Normalise).Where(value => value.Length >= 3).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (postcode.Length > 0 && sitePostcode.Length > 0)
            {
                if (name.Length >= 4 && siteName == name && sitePostcode == postcode)
                    strong.Add((site, 98, "Matched by site name and postcode."));
                if (driverName.Length >= 4 && siteDriverName == driverName && sitePostcode == postcode)
                    strong.Add((site, 97, "Matched by driver text name and postcode."));
                if (address.Length >= 10 && siteAddress == address && sitePostcode == postcode)
                    strong.Add((site, 96, "Matched by collection address and postcode."));
            }

            if (map.Length >= 8 && siteMap.Length >= 8 && siteMap == map)
                strong.Add((site, 94, "Matched by map link."));

            foreach (var alias in incomingAliasCandidates)
            {
                if (siteName == alias || siteDriverName == alias || siteAliases.Contains(alias))
                    strong.Add((site, postcode.Length == 0 ? 88 : 93, postcode.Length == 0 ? "Matched by alias without postcode." : "Matched by alias."));
            }
        }

        var grouped = strong
            .GroupBy(match => match.Site.Id)
            .Select(group => group.OrderByDescending(match => match.Confidence).First())
            .OrderByDescending(match => match.Confidence)
            .ToList();

        if (grouped.Count == 1)
        {
            var match = grouped[0];
            return new SiteIdentityResolution(match.Site, "matched", match.Confidence, match.Reason, false, false, []);
        }

        if (grouped.Count > 1)
        {
            return new SiteIdentityResolution(null, "conflict", grouped[0].Confidence, "Multiple live sites match this workbook row. Hold for review before writing.", false, true, grouped.Select(match => match.Site).ToList());
        }

        var weakDuplicates = FindWeakDuplicates(incoming, sites).ToList();
        if (weakDuplicates.Count > 0)
        {
            return new SiteIdentityResolution(null, "review", 70, "Possible existing site found by weak name/address similarity. Hold for review before creating.", false, true, weakDuplicates);
        }

        var hasStrongCreateData = !string.IsNullOrWhiteSpace(incoming.Name) &&
            (!string.IsNullOrWhiteSpace(postcode) || address.Length >= 12 || !string.IsNullOrWhiteSpace(incoming.MapLink));

        return new SiteIdentityResolution(null, hasStrongCreateData ? "new" : "weak", hasStrongCreateData ? 80 : 30,
            hasStrongCreateData ? "No live match found and row has enough site detail to create." : "No live match found and row lacks enough site detail to create safely.",
            hasStrongCreateData, !hasStrongCreateData, []);
    }

    public static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string NormaliseAddress(string? value)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim().ToUpperInvariant();
        text = text.Replace(" ROAD", " RD").Replace(" STREET", " ST").Replace(" AVENUE", " AVE").Replace(" INDUSTRIAL ESTATE", " IND EST");
        return new(text.Where(char.IsLetterOrDigit).ToArray());
    }

    public static string ExtractPostcode(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, @"\b[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}\b", RegexOptions.IgnoreCase);
        return match.Success ? Normalise(match.Value) : string.Empty;
    }

    private static string NormaliseMap(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        text = text.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .TrimEnd('/');
        return Normalise(text);
    }

    private static IEnumerable<string> SplitAliases(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (var alias in value.Split(new char[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!string.IsNullOrWhiteSpace(alias)) yield return alias.Trim();
    }

    private static IEnumerable<Site> FindWeakDuplicates(IncomingSiteIdentity incoming, IEnumerable<Site> sites)
    {
        var incomingNames = new[] { incoming.Name, incoming.DriverTextName }.Concat(SplitAliases(incoming.Aliases)).Select(Normalise).Where(value => value.Length >= 5).ToList();
        var incomingPostcode = ExtractPostcode(incoming.CollectionAddress);
        var incomingAddress = NormaliseAddress(incoming.CollectionAddress);

        foreach (var site in sites)
        {
            var sitePostcode = ExtractPostcode(site.CollectionAddress);
            var siteNames = new[] { site.Name, site.DriverTextName, site.Aliases }.Where(value => !string.IsNullOrWhiteSpace(value)).SelectMany(value => SplitAliases(value).Append(value!)).Select(Normalise).Where(value => value.Length >= 5).ToList();
            var nameOverlap = incomingNames.Any(left => siteNames.Any(right => left.Contains(right, StringComparison.OrdinalIgnoreCase) || right.Contains(left, StringComparison.OrdinalIgnoreCase)));
            var postcodeOverlap = incomingPostcode.Length > 0 && sitePostcode.Length > 0 && incomingPostcode == sitePostcode;
            var addressOverlap = incomingAddress.Length >= 12 && NormaliseAddress(site.CollectionAddress).Length >= 12 && (incomingAddress.Contains(NormaliseAddress(site.CollectionAddress), StringComparison.OrdinalIgnoreCase) || NormaliseAddress(site.CollectionAddress).Contains(incomingAddress, StringComparison.OrdinalIgnoreCase));

            if ((nameOverlap && postcodeOverlap) || addressOverlap)
                yield return site;
        }
    }

    public static string MergeAliases(params string?[] values)
    {
        var aliases = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
            foreach (var alias in SplitAliases(value))
                aliases.Add(alias.Trim());
        return string.Join(", ", aliases);
    }

    public static string ToJson(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });
}
