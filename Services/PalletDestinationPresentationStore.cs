using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record PalletDestinationPresentation(
    string Region,
    string DisplayName,
    string? SiteCode,
    bool MasterMatched);

/// <summary>
/// Resolves the planning-board presentation of delivery destinations from Site Master.
/// Pallet Control must remain recognisable to planners, so raw order text is retained as
/// the key while the visible heading can use a shorter configured site alias.
/// </summary>
public static class PalletDestinationPresentationStore
{
    public static async Task<Dictionary<string, PalletDestinationPresentation>> ResolveAsync(
        TmsDbContext db,
        IEnumerable<string> destinations,
        CancellationToken ct)
    {
        var destinationList = destinations
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sites = await db.Sites.AsNoTracking()
            .Where(site => site.Active)
            .Take(5000)
            .ToListAsync(ct);

        // Aliases, driver names, coordinates and workbook detail can live in the audited
        // master-detail register. Enrich before matching so Pallet Control uses the same
        // Site Master identity that the planner, geofences and SLH Assistant use.
        try
        {
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Base Site rows still contain ExternalCode, Name, address and OperationalRegion,
            // so presentation remains useful even if legacy detail enrichment is unavailable.
            db.ChangeTracker.Clear();
        }

        Dictionary<string, string> fallbackRegions;
        try
        {
            fallbackRegions = await SitePlanningProfileStore.ResolveRegionsAsync(db, destinationList, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            fallbackRegions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, PalletDestinationPresentation>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in destinationList)
        {
            var site = MatchSite(sites, destination);
            var fallback = fallbackRegions.GetValueOrDefault(destination, "Other");
            var region = ResolveRegion(site, destination, fallback);
            var displayName = ResolveDisplayName(site, destination);
            result[destination] = new PalletDestinationPresentation(region, displayName, site?.ExternalCode, site is not null);
        }

        return result;
    }

    public static string InferOperationalRegion(string? value)
    {
        var text = (value ?? string.Empty).ToUpperInvariant();
        var token = text.Split(new[] { ' ', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.Any(char.IsDigit) && part.Any(char.IsLetter)) ?? string.Empty;
        var prefix = new string(token.TakeWhile(char.IsLetter).ToArray());

        if (new[] { "NE", "NW", "CA", "DL", "DH", "LA", "PR", "FY", "BB", "BD", "HD", "HX", "LS", "YO", "HG", "HU", "DN", "S", "SR", "TS", "WF" }.Contains(prefix)) return "North";
        if (new[] { "B", "CV", "DE", "DY", "LE", "LN", "NG", "NN", "ST", "TF", "WS", "WV", "WR" }.Contains(prefix)) return "Midlands";
        if (new[] { "CB", "CM", "CO", "IP", "NR", "PE", "SG", "SS", "AL", "EN", "IG", "RM" }.Contains(prefix)) return "East";
        if (new[] { "BS", "BA", "GL", "HR", "NP", "CF", "LD", "SA" }.Contains(prefix)) return "West / Wales";
        if (new[] { "BH", "DT", "EX", "PL", "TQ", "TA", "TR" }.Contains(prefix)) return "South West";
        if (new[] { "BN", "CT", "DA", "GU", "HA", "HP", "KT", "ME", "MK", "OX", "PO", "RG", "RH", "SL", "SM", "SO", "SP", "TN", "TW" }.Contains(prefix)) return "South East";
        if (new[] { "E", "EC", "N", "SE", "SW", "W", "WC" }.Contains(prefix)) return "London";
        return "Other";
    }

    private static string ResolveRegion(Site? site, string destination, string fallback)
    {
        var explicitRegion = CanonicalRegion(site?.OperationalRegion);
        if (explicitRegion is not null && explicitRegion != "Other") return explicitRegion;

        var inferred = InferOperationalRegion(site?.CollectionAddress);
        if (inferred != "Other") return inferred;

        var fallbackRegion = CanonicalRegion(fallback);
        if (fallbackRegion is not null && fallbackRegion != "Other") return fallbackRegion;

        inferred = InferOperationalRegion(destination);
        if (inferred != "Other") return inferred;

        return explicitRegion ?? fallbackRegion ?? "Other";
    }

    private static string ResolveDisplayName(Site? site, string destination)
    {
        if (site is null) return destination;

        // The alias field is deliberately planner-controlled. Use the shortest sensible alias
        // only when it genuinely saves width; otherwise retain the familiar source/site text.
        var alias = SplitAliases(site.Aliases)
            .Where(value => value.Length >= 3 && value.Length <= 40 && value.Any(char.IsLetter))
            .Where(value => value.Length < destination.Length)
            .OrderBy(value => value.Length)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(alias)) return alias;

        if (!string.IsNullOrWhiteSpace(site.DriverTextName) && site.DriverTextName.Trim().Length < destination.Length)
            return site.DriverTextName.Trim();

        if (!string.IsNullOrWhiteSpace(site.Name) && site.Name.Trim().Length < destination.Length)
            return site.Name.Trim();

        return destination;
    }

    private static Site? MatchSite(IEnumerable<Site> sites, string value)
    {
        var key = Normalise(value);
        if (key.Length == 0) return null;

        // Prefer exact matches first. This avoids a short alias stealing a destination that
        // belongs to another Site Master row.
        var exact = sites.FirstOrDefault(site => IdentityValues(site)
            .Select(Normalise)
            .Any(candidate => candidate.Length > 0 && candidate == key));
        if (exact is not null) return exact;

        return sites.FirstOrDefault(site => IdentityValues(site)
            .Select(Normalise)
            .Where(candidate => candidate.Length >= 5)
            .Any(candidate => key.Contains(candidate, StringComparison.OrdinalIgnoreCase)
                || candidate.Contains(key, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> IdentityValues(Site site)
    {
        yield return site.ExternalCode;
        yield return site.Name;
        if (!string.IsNullOrWhiteSpace(site.DriverTextName)) yield return site.DriverTextName;
        foreach (var alias in SplitAliases(site.Aliases)) yield return alias;
    }

    private static IEnumerable<string> SplitAliases(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(new[] { ';', ',', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(alias => alias.Trim())
            .Where(alias => alias.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? CanonicalRegion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalised = new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return normalised switch
        {
            "NORTH" => "North",
            "MIDLANDS" or "MIDLAND" => "Midlands",
            "EAST" or "EASTERN" => "East",
            "LONDON" => "London",
            "SOUTHEAST" => "South East",
            "SOUTHWEST" => "South West",
            "WESTWALES" or "WALESWEST" or "WALES" => "West / Wales",
            "OTHER" => "Other",
            _ => value.Trim()
        };
    }

    private static string Normalise(string? value)
        => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
