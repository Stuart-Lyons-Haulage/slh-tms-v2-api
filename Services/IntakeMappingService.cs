using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public sealed class IntakeMappingService(TmsDbContext db)
{
    public async Task<IntakeSiteMatch> MatchSiteAsync(string originalValue, CancellationToken ct)
    {
        var normalized = Normalize(originalValue);
        if (normalized.Length == 0)
            return new(false, true, null, null, null, originalValue, "missing_site", 0m);

        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var direct = sites.Where(x => Normalize(x.Name) == normalized || Normalize(x.ExternalCode) == normalized).ToList();
        if (direct.Count == 1) return Result(direct[0], originalValue, 1m);
        if (direct.Count > 1) return new(false, true, null, null, null, originalValue, "ambiguous_site", 0m);

        var mappings = await db.IntegrationMappings.AsNoTracking()
            .Where(x => x.Active && x.TmsEntityType == "Site" && x.MappingKind == "SiteAlias")
            .ToListAsync(ct);
        var matching = mappings.Where(x => Normalize(x.NormalizedExternalValue ?? x.ExternalKey) == normalized)
            .Select(x => x.TmsEntityId).Distinct().ToList();
        if (matching.Count != 1)
            return new(false, true, null, null, null, originalValue, matching.Count > 1 ? "ambiguous_site_alias" : "unknown_site", 0m);

        var site = sites.SingleOrDefault(x => x.Id == matching[0]);
        return site is null
            ? new(false, true, null, null, null, originalValue, "inactive_or_missing_canonical_site", 0m)
            : Result(site, originalValue, mappings.Where(x => x.TmsEntityId == site.Id && Normalize(x.NormalizedExternalValue ?? x.ExternalKey) == normalized)
                .Max(x => x.ConfidenceThreshold ?? 1m));
    }

    public static string Normalize(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IntakeSiteMatch Result(Slh.Tms.Api.Models.Site site, string original, decimal confidence) =>
        new(true, false, site.Id, site.Name, site.OperationalRegion, original, null, confidence);
}

public sealed record IntakeSiteMatch(bool Matched, bool RequiresReview, Guid? EntityId, string? CanonicalValue,
    string? OperationalRegion, string OriginalValue, string? IssueCode, decimal Confidence);
