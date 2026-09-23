using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record SiteGeofenceSyncResult(
    int SitesCoded,
    int GeofencesLinked,
    int GeofencesUnlinked,
    int GeofencesCanonicalized,
    int SitesMissingGeofence,
    IReadOnlyList<SiteGeofenceStatus> Sites,
    IReadOnlyList<string>? Warnings = null);

public sealed record SiteGeofenceStatus(
    Guid SiteId,
    string SiteCode,
    string SiteName,
    IReadOnlyList<string> LinkedGeofences,
    bool GeofenceLinked,
    bool NeedsReview);

public static partial class SiteGeofenceMasterSync
{
    private const string LocationOnly = "LOCATION_ONLY";

    private static readonly HashSet<string> IgnoredTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "SITE", "DEPOT", "WAREHOUSE", "DISTRIBUTION", "CENTRE", "CENTER", "STORE", "MARKET",
        "ROAD", "STREET", "LANE", "UNIT", "SERVICE", "SERVICES", "LIMITED", "LTD", "THE", "AND"
    };

    // A fuzzy match is accepted for automatic linking only when the token-overlap similarity
    // score reaches this threshold (0-100). 70 means at least 70% of the meaningful tokens
    // in the shorter name appear in the longer name. This is deliberately conservative to
    // avoid false positives; a human operator can always confirm lower-confidence suggestions
    // via the Geofence Integrity screen.
    private const int FuzzyAutoLinkThreshold = 70;

    // When a geofence is linked to a site via fuzzy matching, the geofence name is written
    // as an alias on the site so that future syncs match exactly and the alias appears in
    // the Geofence Integrity UI for operator awareness.
    private const int FuzzyAliasThreshold = 55;

    public static async Task<SiteGeofenceSyncResult> SyncAsync(TmsDbContext db, CancellationToken ct)
    {
        var sites = await db.Sites.Where(x => x.Active).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        try
        {
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            sites = await db.Sites.Where(x => x.Active).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        }

        var fences = await db.SiteGeofences.Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct);
        var warnings = new List<string>();
        var sitesCoded = 0;
        try
        {
            sitesCoded = await CanonicalizeSiteCodesAsync(db, sites, fences, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Site code canonicalisation was skipped: {ex.GetBaseException().Message}");
            db.ChangeTracker.Clear();
            sites = await db.Sites.Where(x => x.Active).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
            fences = await db.SiteGeofences.Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct);
        }

        var sitesById = sites.ToDictionary(x => x.Id);
        var linked = 0;
        var unlinked = 0;
        var canonicalized = 0;

        foreach (var fence in fences)
        {
            if (string.Equals(fence.SiteNumber?.Trim(), LocationOnly, StringComparison.OrdinalIgnoreCase))
                continue;

            // An explicit persisted SiteId is authoritative. Operators use the Geofence
            // dropdown to confirm the physical Site relationship, so do not reject or
            // later undo that choice merely because provider/geofence naming differs.
            if (fence.SiteId is Guid linkedSiteId &&
                sitesById.TryGetValue(linkedSiteId, out var linkedSite))
            {
                if (!string.Equals(fence.SiteNumber, linkedSite.ExternalCode, StringComparison.OrdinalIgnoreCase))
                {
                    fence.SiteNumber = linkedSite.ExternalCode;
                    canonicalized++;
                }
                fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
                continue;
            }

            // Only genuinely unlinked geofences are candidates for automatic matching.
            // Automatic matching remains conservative and still requires one unique best match.
            var candidates = MatchingSites(fence.Name, sites);
            if (candidates.Count == 1)
            {
                var site = candidates[0];
                if (fence.SiteId != site.Id)
                {
                    fence.SiteId = site.Id;
                    linked++;
                }
                if (!string.Equals(fence.SiteNumber, site.ExternalCode, StringComparison.OrdinalIgnoreCase))
                {
                    fence.SiteNumber = site.ExternalCode;
                    canonicalized++;
                }
                fence.UpdatedAtUtc = DateTimeOffset.UtcNow;

                // If this link was established via fuzzy matching (geofence name doesn't
                // appear in the site's canonical Name, DriverTextName or existing aliases),
                // register the geofence name as an alias on the site. This means:
                //   (a) future syncs will match exactly without needing fuzzy logic, and
                //   (b) the alias appears in the Geofence Integrity UI so operators can
                //       review what was auto-merged and remove it if incorrect.
                var dbSite = sitesById.GetValueOrDefault(site.Id);
                if (dbSite is not null && ShouldRegisterAlias(fence.Name, dbSite))
                    RegisterAlias(fence.Name, dbSite);

                continue;
            }

            if (fence.SiteId is not null || !string.IsNullOrWhiteSpace(fence.SiteNumber))
            {
                fence.SiteId = null;
                fence.SiteNumber = null;
                fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
                unlinked++;
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Automatic geofence link updates were skipped: {ex.GetBaseException().Message}");
            db.ChangeTracker.Clear();
            sites = await db.Sites.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
            fences = await db.SiteGeofences.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct);
            linked = 0;
            unlinked = 0;
            canonicalized = 0;
        }

        var status = BuildStatus(sites, fences);
        return new SiteGeofenceSyncResult(
            sitesCoded,
            linked,
            unlinked,
            canonicalized,
            status.Count(x => x.NeedsReview),
            status,
            warnings);
    }

    public static async Task<IReadOnlyList<SiteGeofenceStatus>> GetStatusAsync(TmsDbContext db, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).OrderBy(x => x.ExternalCode).ThenBy(x => x.Name).ToListAsync(ct);
        try
        {
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sites = await db.Sites.AsNoTracking().Where(x => x.Active).OrderBy(x => x.ExternalCode).ThenBy(x => x.Name).ToListAsync(ct);
        }
        var fences = await db.SiteGeofences.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct);
        return BuildStatus(sites, fences);
    }

    public static async Task<SiteGeofenceStatus> LinkGeofenceAsync(TmsDbContext db, Guid geofenceId, string siteCode, CancellationToken ct)
    {
        var sites = await db.Sites.Where(x => x.Active).ToListAsync(ct);
        try { await MasterDetailStore.EnrichSitesAsync(db, sites, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { }

        var requested = sites.FirstOrDefault(x => string.Equals(x.ExternalCode, siteCode.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("Site code not found.");
        var fence = await ResolveLinkableGeofenceAsync(db, geofenceId, ct);

        // This method is called by the authenticated operator dropdown. The selected
        // canonical Site is therefore an explicit manual decision and is authoritative.
        // Name matching is reserved for automatic linking only.
        fence.SiteId = requested.Id;
        fence.SiteNumber = requested.ExternalCode;
        fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return BuildStatus(sites, new[] { fence }).First(x => x.SiteId == requested.Id);
    }

    private static async Task<SiteGeofence> ResolveLinkableGeofenceAsync(TmsDbContext db, Guid geofenceId, CancellationToken ct)
    {
        var fence = await db.SiteGeofences.FirstOrDefaultAsync(x => x.Id == geofenceId && x.Active, ct);
        if (fence is not null) return fence;

        var embedded = EmbeddedGeofenceEngine.ApprovedFences.FirstOrDefault(x => x.Id == geofenceId)
            ?? throw new KeyNotFoundException("Geofence not found.");
        var normalizedName = NormalizeName(embedded.Name);
        fence = await db.SiteGeofences.FirstOrDefaultAsync(x => x.NormalizedName == normalizedName, ct);
        if (fence is null)
        {
            fence = new SiteGeofence
            {
                Id = embedded.Id,
                Name = embedded.Name,
                NormalizedName = normalizedName,
                Category = embedded.Category,
                CategoryMaxWaitMinutes = embedded.CategoryMaxWaitMinutes,
                MaxWaitMinutes = embedded.MaxWaitMinutes,
                PendingEntryMinutes = embedded.PendingEntryMinutes,
                PendingExitMinutes = embedded.PendingExitMinutes,
                SiteNumber = embedded.SiteNumber,
                PolygonJson = PolygonJson(embedded),
                Active = true
            };
            db.SiteGeofences.Add(fence);
            return fence;
        }

        fence.Active = true;
        fence.Name = embedded.Name;
        fence.NormalizedName = normalizedName;
        fence.Category ??= embedded.Category;
        fence.CategoryMaxWaitMinutes ??= embedded.CategoryMaxWaitMinutes;
        fence.MaxWaitMinutes ??= embedded.MaxWaitMinutes;
        fence.PendingEntryMinutes = embedded.PendingEntryMinutes;
        fence.PendingExitMinutes = embedded.PendingExitMinutes;
        if (string.IsNullOrWhiteSpace(fence.PolygonJson)) fence.PolygonJson = PolygonJson(embedded);
        return fence;
    }

    internal static bool NameConfirms(string geofenceName, Site site)
        => MatchScore(geofenceName, site) > 0;

    private static List<Site> MatchingSites(string geofenceName, IReadOnlyList<Site> sites)
    {
        // Phase 1 — exact token overlap (existing behaviour, score > 0 means ≥1 shared token)
        var scored = sites
            .Select(site => new { Site = site, Score = MatchScore(geofenceName, site) })
            .Where(x => x.Score > 0)
            .ToList();
        if (scored.Count > 0)
        {
            var bestScore = scored.Max(x => x.Score);
            var best = scored.Where(x => x.Score == bestScore).Select(x => x.Site).ToList();
            if (best.Count == 1) return best;
            // Multiple sites share the same top token-overlap score — fall through to fuzzy
            // ranking to break the tie.
        }

        // Phase 2 — fuzzy similarity score (same algorithm as GeofenceLinkDiagnostics).
        // Only returns a result when exactly ONE site clears the auto-link threshold so
        // we never automatically link an ambiguous geofence.
        var fuzzy = sites
            .Select(site => new { Site = site, Score = FuzzyScore(geofenceName, site) })
            .Where(x => x.Score >= FuzzyAutoLinkThreshold)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Site.Name)
            .ToList();
        if (fuzzy.Count == 1) return [fuzzy[0].Site];

        // If multiple sites are above threshold (ambiguous) return them all so the caller
        // treats this as a non-unique match and leaves the geofence unlinked.
        if (fuzzy.Count > 1) return fuzzy.Select(x => x.Site).ToList();

        return [];
    }

    /// <summary>
    /// Token-overlap similarity score (0–100). Meaningful tokens (≥2 chars, not in
    /// IgnoredTokens, not purely numeric) are extracted from both names; the score is
    /// the percentage of the smaller token set that appears in the larger set.
    /// </summary>
    private static int FuzzyScore(string geofenceName, Site site)
    {
        var fenceTokens = FuzzyTokens(geofenceName);
        if (fenceTokens.Count == 0) return 0;
        return SiteNames(site)
            .Select(name => FuzzyTokens(name))
            .Select(siteTokens =>
            {
                if (siteTokens.Count == 0) return 0;
                var common = fenceTokens.Intersect(siteTokens, StringComparer.OrdinalIgnoreCase).Count();
                if (common == 0) return 0;
                return (int)Math.Round(100d * common / Math.Max(fenceTokens.Count, siteTokens.Count));
            })
            .DefaultIfEmpty(0)
            .Max();
    }

    private static List<string> FuzzyTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var spaced = new string(value.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ').ToArray());
        return spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2 && !IgnoredTokens.Contains(token) && !token.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int MatchScore(string geofenceName, Site site)
    {
        var fenceTokens = Tokens(geofenceName);
        if (fenceTokens.Count == 0) return 0;
        return SiteNames(site)
            .Select(Tokens)
            .Select(tokens => tokens.Count(fenceTokens.Contains))
            .DefaultIfEmpty(0)
            .Max();
    }

    private static string NormalizeName(string value) => string.Join(' ', value.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string PolygonJson(EmbeddedFence fence) =>
        JsonSerializer.Serialize(fence.Points.Select(point => new[] { point.Longitude, point.Latitude }));

    private static async Task<int> CanonicalizeSiteCodesAsync(TmsDbContext db, IReadOnlyList<Site> sites, IReadOnlyList<SiteGeofence> fences, CancellationToken ct)
    {
        var used = new HashSet<int>();
        var retained = new Dictionary<Guid, int>();
        foreach (var site in sites)
        {
            var match = SiteCodeRegex().Match(site.ExternalCode?.Trim() ?? string.Empty);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var number) || number <= 0 || !used.Add(number))
                continue;
            retained[site.Id] = number;
        }

        var next = 1;
        var desiredBySiteId = new Dictionary<Guid, string>();
        foreach (var site in sites)
        {
            if (!retained.TryGetValue(site.Id, out var number))
            {
                while (used.Contains(next)) next++;
                number = next;
                used.Add(number);
                next++;
            }

            var desired = $"SITE{number:D3}";
            desiredBySiteId[site.Id] = desired;
        }

        var changedSites = sites
            .Where(site => desiredBySiteId.TryGetValue(site.Id, out var desired) &&
                !string.Equals(site.ExternalCode, desired, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (changedSites.Count == 0) return 0;

        if (db.Database.IsRelational())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await ApplySiteCodeRenumberingAsync(db, changedSites, fences, desiredBySiteId, ct);
            await transaction.CommitAsync(ct);
        }
        else
        {
            await ApplySiteCodeRenumberingAsync(db, changedSites, fences, desiredBySiteId, ct);
        }

        return changedSites.Count;
    }

    private static async Task ApplySiteCodeRenumberingAsync(
        TmsDbContext db,
        IReadOnlyList<Site> changedSites,
        IReadOnlyList<SiteGeofence> fences,
        IReadOnlyDictionary<Guid, string> desiredBySiteId,
        CancellationToken ct)
    {
        foreach (var site in changedSites)
            site.ExternalCode = TemporarySiteCode(site.Id);

        await db.SaveChangesAsync(ct);

        foreach (var site in changedSites)
        {
            var desired = desiredBySiteId[site.Id];
            site.ExternalCode = desired;
            foreach (var fence in fences.Where(x => x.SiteId == site.Id))
                fence.SiteNumber = desired;
        }

        await db.SaveChangesAsync(ct);
    }

    private static string TemporarySiteCode(Guid siteId) => $"TMP{siteId:N}"[..35];

    private static IReadOnlyList<SiteGeofenceStatus> BuildStatus(IReadOnlyList<Site> sites, IEnumerable<SiteGeofence> fences)
    {
        var fenceList = fences.ToList();
        return sites
            .OrderBy(x => ParseSiteNumber(x.ExternalCode))
            .ThenBy(x => x.Name)
            .Select(site =>
            {
                var linked = fenceList
                    .Where(fence => fence.SiteId == site.Id)
                    .Select(fence => fence.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToList();
                return new SiteGeofenceStatus(site.Id, site.ExternalCode, site.Name, linked, linked.Count > 0, linked.Count == 0);
            })
            .ToList();
    }

    private static int ParseSiteNumber(string? code)
    {
        var match = SiteCodeRegex().Match(code?.Trim() ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : int.MaxValue;
    }

    private static IEnumerable<string?> SiteNames(Site site)
    {
        yield return site.Name;
        yield return site.DriverTextName;
        if (!string.IsNullOrWhiteSpace(site.Aliases))
        {
            foreach (var alias in site.Aliases.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return alias;
        }
    }

    private static HashSet<string> Tokens(string? value)
        => Regex.Matches(value ?? string.Empty, "[A-Za-z0-9]+")
            .Cast<Match>()
            .Select(match => match.Value.ToUpperInvariant())
            .Where(token => token.Length >= 4 && !IgnoredTokens.Contains(token) && !token.All(char.IsDigit))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);


    /// <summary>
    /// Returns true when the geofence name is not already represented in the site's
    /// Name, DriverTextName or alias list — i.e. when the fuzzy match introduced new
    /// naming evidence that should be recorded.
    /// </summary>
    private static bool ShouldRegisterAlias(string geofenceName, Site site)
    {
        var key = NormalizeAliasKey(geofenceName);
        if (key.Length == 0) return false;
        if (NormalizeAliasKey(site.Name) == key || NormalizeAliasKey(site.DriverTextName) == key) return false;
        foreach (var existing in SplitAliases(site.Aliases))
            if (NormalizeAliasKey(existing) == key) return false;
        return true;
    }

    /// <summary>
    /// Appends the geofence name to the site's alias list, which is stored as a
    /// comma-separated string on the Site entity.
    /// </summary>
    private static void RegisterAlias(string geofenceName, Site site)
    {
        var trimmed = geofenceName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;
        site.Aliases = string.IsNullOrWhiteSpace(site.Aliases)
            ? trimmed
            : string.Join(", ", SplitAliases(site.Aliases).Append(trimmed).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SplitAliases(string? aliases) =>
        string.IsNullOrWhiteSpace(aliases)
            ? []
            : aliases.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeAliasKey(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    [GeneratedRegex("^SITE0*([1-9][0-9]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SiteCodeRegex();
}
