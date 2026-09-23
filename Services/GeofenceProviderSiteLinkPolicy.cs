using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// DOT/Falcon's reviewed 736-geofence export uses site_no=1 on hundreds of unrelated
/// fences. That value is a provider/default placeholder, not SLH Site Reference 1.
/// Canonical operational site identity must come from SLH Site Master (code/name/alias)
/// or an explicit manual link, never from this provider placeholder.
/// </summary>
public static class GeofenceProviderSiteLinkPolicy
{
    public const string ProviderUnassignedSiteNumber = "1";

    public static bool IsProviderPlaceholder(string? value) =>
        string.Equals(value?.Trim(), ProviderUnassignedSiteNumber, StringComparison.OrdinalIgnoreCase);

    public static string? SanitizeProviderSiteNumber(string? value)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return IsProviderPlaceholder(clean) ? null : clean;
    }

    public static Site? ExactCanonicalSite(string geofenceName, string? providerSiteNumber, IReadOnlyCollection<Site> sites)
    {
        var cleanCode = SanitizeProviderSiteNumber(providerSiteNumber);
        if (!string.IsNullOrWhiteSpace(cleanCode))
        {
            var byCode = DistinctSites(sites.Where(site => CodesEquivalent(site.ExternalCode, cleanCode)));
            if (byCode.Count == 1) return byCode[0];
            if (byCode.Count > 1) return null;
        }

        var fenceName = Normalize(geofenceName);
        var byName = DistinctSites(sites.Where(site =>
            Normalize(site.Name) == fenceName || Normalize(site.DriverTextName) == fenceName));
        return byName.Count == 1 ? byName[0] : null;
    }

    private static List<Site> DistinctSites(IEnumerable<Site> values) =>
        values.GroupBy(site => site.Id).Select(group => group.First()).ToList();

    private static bool CodesEquivalent(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        return long.TryParse(a, out var an) && long.TryParse(b, out var bn) && an == bn;
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

/// <summary>
/// Idempotently repairs historical SiteGeofence rows created when provider site_no=1
/// was incorrectly interpreted as SLH Site 1. Geometry, geofence IDs and visit history
/// are never deleted or rewritten. Exact Site Master name/alias matches are retained;
/// otherwise the false canonical link is cleared for explicit reconciliation.
/// </summary>
public static class GeofenceProviderPlaceholderRepair
{
    public static async Task<GeofenceProviderPlaceholderRepairResult> EnsureAsync(TmsDbContext db, CancellationToken ct)
    {
        List<SiteGeofence> suspects;
        try
        {
            suspects = await db.SiteGeofences
                .Where(fence => fence.Active && fence.SiteNumber != null)
                .ToListAsync(ct);
            suspects = suspects
                .Where(fence => GeofenceProviderSiteLinkPolicy.IsProviderPlaceholder(fence.SiteNumber))
                .ToList();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return new GeofenceProviderPlaceholderRepairResult(0, 0, 0);
        }

        if (suspects.Count == 0) return new GeofenceProviderPlaceholderRepairResult(0, 0, 0);

        List<Site> sites;
        try
        {
            sites = await GeofenceSiteResolver.LoadActiveSitesAsync(db, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return new GeofenceProviderPlaceholderRepairResult(suspects.Count, 0, 0);
        }

        var relinked = 0;
        var cleared = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var fence in suspects)
        {
            var target = GeofenceProviderSiteLinkPolicy.ExactCanonicalSite(fence.Name, null, sites);
            if (target is not null)
            {
                if (fence.SiteId == target.Id && string.Equals(fence.SiteNumber, target.ExternalCode, StringComparison.OrdinalIgnoreCase))
                    continue;

                var beforeSiteId = fence.SiteId;
                fence.SiteId = target.Id;
                fence.SiteNumber = target.ExternalCode;
                fence.UpdatedAtUtc = now;
                relinked++;
                Audit(db, fence, "RepairedProviderPlaceholderSiteLink", new
                {
                    providerSiteNumber = GeofenceProviderSiteLinkPolicy.ProviderUnassignedSiteNumber,
                    previousSiteId = beforeSiteId,
                    canonicalSiteId = target.Id,
                    canonicalSiteCode = target.ExternalCode,
                    canonicalSiteName = target.Name
                });
                continue;
            }

            var previousSiteId = fence.SiteId;
            fence.SiteId = null;
            fence.SiteNumber = null;
            fence.UpdatedAtUtc = now;
            cleared++;
            Audit(db, fence, "ClearedProviderPlaceholderSiteLink", new
            {
                providerSiteNumber = GeofenceProviderSiteLinkPolicy.ProviderUnassignedSiteNumber,
                previousSiteId,
                reason = "DOT/Falcon site_no=1 is a provider placeholder and no unique exact Site Master name/alias matched this geofence."
            });
        }

        if (relinked > 0 || cleared > 0)
            await db.SaveChangesAsync(ct);

        return new GeofenceProviderPlaceholderRepairResult(suspects.Count, relinked, cleared);
    }

    private static void Audit(TmsDbContext db, SiteGeofence fence, string action, object changes)
    {
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Geofence",
            EntityId = fence.Id,
            Action = action,
            ChangedBy = "GeofenceProviderPlaceholderRepair",
            ChangesJson = JsonSerializer.Serialize(changes)
        });
    }
}

/// <summary>
/// Repairs active Falcon geofences that have no canonical Site link by matching the
/// geofence name against one unique Site Master code/name/driver-text/alias. This makes
/// the Site Master alias field operational rather than display-only and repairs aliases
/// that were saved before the geofence was imported or linked.
/// </summary>
public static class GeofenceSiteAliasRepair
{
    public static async Task<int> EnsureAsync(TmsDbContext db, CancellationToken ct)
    {
        List<SiteGeofence> unlinked;
        try
        {
            unlinked = await db.SiteGeofences
                .Where(fence => fence.Active && fence.SiteId == null)
                .ToListAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return 0;
        }

        if (unlinked.Count == 0) return 0;

        List<Site> sites;
        try
        {
            // GeofenceSiteResolver expands every unique Site Master alias into a synthetic
            // driver-text candidate while retaining the canonical Site Id/code.
            sites = await GeofenceSiteResolver.LoadActiveSitesAsync(db, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return 0;
        }

        var repaired = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var fence in unlinked)
        {
            // Phase 1: exact canonical match (code, name, driver-text, alias)
            var target = GeofenceProviderSiteLinkPolicy.ExactCanonicalSite(fence.Name, fence.SiteNumber, sites);
            string matchReason;
            if (target is not null)
            {
                matchReason = "Unique exact Site Master name/driver-text/alias match.";
            }
            else
            {
                // Phase 2: fuzzy token-similarity match — only accepts a result when exactly
                // ONE site clears the threshold, preventing ambiguous auto-links.
                var fuzzyResult = GeofenceFuzzyMatcher.BestUniqueMatch(fence.Name, sites);
                if (fuzzyResult is null) continue;
                target = fuzzyResult.Site;
                matchReason = $"Fuzzy token similarity match (score {fuzzyResult.Score}/100). " +
                              "Geofence name registered as site alias for future exact matching.";

                // Register the geofence name as an alias on the site so future syncs
                // match exactly and the alias is visible in the Geofence Integrity UI.
                GeofenceFuzzyMatcher.RegisterAliasIfNew(fence.Name, target);
            }

            fence.SiteId = target.Id;
            fence.SiteNumber = target.ExternalCode;
            fence.UpdatedAtUtc = now;
            repaired++;
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Geofence",
                EntityId = fence.Id,
                Action = "LinkedFromSiteAlias",
                ChangedBy = "GeofenceSiteAliasRepair",
                ChangesJson = JsonSerializer.Serialize(new
                {
                    geofenceName = fence.Name,
                    canonicalSiteId = target.Id,
                    canonicalSiteCode = target.ExternalCode,
                    canonicalSiteName = target.Name,
                    reason = matchReason
                })
            });
        }

        if (repaired > 0) await db.SaveChangesAsync(ct);
        return repaired;
    }
}

public sealed record GeofenceProviderPlaceholderRepairResult(int Found, int Relinked, int Cleared);

/// <summary>
/// Shared fuzzy name-matching logic for geofence-to-site linking.
/// Kept separate so it can be used by both SiteGeofenceMasterSync and
/// GeofenceSiteAliasRepair without duplication.
/// </summary>
public static class GeofenceFuzzyMatcher
{
    // Mirrors SiteGeofenceMasterSync.FuzzyAutoLinkThreshold.
    // Require 70% token overlap for an automatic link.
    public const int AutoLinkThreshold = 70;

    private static readonly HashSet<string> IgnoredTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "SITE", "DEPOT", "WAREHOUSE", "DISTRIBUTION", "CENTRE", "CENTER", "STORE", "MARKET",
        "ROAD", "STREET", "LANE", "UNIT", "SERVICE", "SERVICES", "LIMITED", "LTD", "THE", "AND"
    };

    public sealed record FuzzyMatch(Site Site, int Score);

    /// <summary>
    /// Returns the single site that best matches the geofence name above the auto-link
    /// threshold, or null if no site qualifies or more than one site is tied at the top.
    /// </summary>
    public static FuzzyMatch? BestUniqueMatch(string geofenceName, IReadOnlyList<Site> sites)
    {
        var fenceTokens = Tokens(geofenceName);
        if (fenceTokens.Count == 0) return null;

        var scored = sites
            .Select(site => new FuzzyMatch(site, ScoreAgainst(fenceTokens, site)))
            .Where(x => x.Score >= AutoLinkThreshold)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Site.Name)
            .ToList();

        return scored.Count == 1 ? scored[0] : null;
    }

    /// <summary>
    /// Appends the geofence name to the site alias list if it is not already represented
    /// in the site's Name, DriverTextName or existing aliases.
    /// </summary>
    public static void RegisterAliasIfNew(string geofenceName, Site site)
    {
        var key = Normalize(geofenceName);
        if (key.Length == 0) return;
        if (Normalize(site.Name) == key || Normalize(site.DriverTextName) == key) return;
        foreach (var alias in SplitAliases(site.Aliases))
            if (Normalize(alias) == key) return;

        var trimmed = geofenceName.Trim();
        site.Aliases = string.IsNullOrWhiteSpace(site.Aliases)
            ? trimmed
            : string.Join(", ", SplitAliases(site.Aliases).Append(trimmed).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static int ScoreAgainst(IReadOnlyCollection<string> fenceTokens, Site site)
    {
        var candidates = new[] { site.Name, site.DriverTextName }
            .Concat(SplitAliases(site.Aliases))
            .Select(Tokens)
            .Where(t => t.Count > 0);

        return candidates
            .Select(siteTokens =>
            {
                var common = fenceTokens.Intersect(siteTokens, StringComparer.OrdinalIgnoreCase).Count();
                if (common == 0) return 0;
                return (int)Math.Round(100d * common / Math.Max(fenceTokens.Count, siteTokens.Count));
            })
            .DefaultIfEmpty(0)
            .Max();
    }

    private static List<string> Tokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var spaced = new string(value.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ').ToArray());
        return spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2 && !IgnoredTokens.Contains(token) && !token.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SplitAliases(string? aliases) =>
        string.IsNullOrWhiteSpace(aliases)
            ? []
            : aliases.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

