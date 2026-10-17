using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/sites")]
[Authorize]
public sealed class SiteAliasController(TmsDbContext db) : ControllerBase
{
    private const string SiteMasterDetailType = "masterdetail:site";
    private const string SiteCutoffDetailType = "masterdetail:sitecutoff";
    private const string SiteTimingRuleDetailType = "masterdetail:sitetimingrule";
    private const string AliasEditorSource = "SLH Site CRM alias editor";
    private const string ReviewNote = "Full workbook detail retained in the audited register for legacy production columns.";

    [HttpGet("{id:guid}/timing-profile")]
    public async Task<IActionResult> GetTimingProfile(Guid id, CancellationToken ct)
    {
        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (site is null) return NotFound();

        var siteTokens = BuildSiteTokens(site).ToList();
        var timingRows = await db.StagedImports.AsNoTracking()
            .Where(item => item.Status == StagingStatus.Promoted &&
                (item.EntityType == SiteCutoffDetailType || item.EntityType == SiteTimingRuleDetailType))
            .OrderByDescending(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .Take(10000)
            .ToListAsync(ct);

        var cutoffs = new List<SiteCutoffTimingDto>();
        var routeTimings = new List<SiteRouteTimingDto>();

        foreach (var row in timingRows)
        {
            if (string.IsNullOrWhiteSpace(row.PayloadJson)) continue;

            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var payload = document.RootElement;

                if (row.EntityType == SiteCutoffDetailType)
                {
                    var siteId = Text(payload, "siteId") ?? Text(payload, "externalCode") ?? Text(payload, "siteCode");
                    var siteName = Text(payload, "siteName") ?? Text(payload, "name");
                    if (!MatchesSite(siteTokens, siteId, siteName, row.IdempotencyKey)) continue;

                    cutoffs.Add(new SiteCutoffTimingDto(
                        SiteId: siteId ?? site.ExternalCode,
                        SiteName: siteName ?? site.Name,
                        MatchedLiveSite: Bool(payload, "matchedLiveSite") ?? MatchesSite(siteTokens, siteId, siteName, row.IdempotencyKey),
                        Plan: Text(payload, "plan"),
                        Temperature: Text(payload, "temperature"),
                        PalletType: Text(payload, "palletType"),
                        StandardCutoff: Text(payload, "standardCutoff"),
                        ExtendedCutoff: Text(payload, "extendedCutoff"),
                        CutoffCheck: Text(payload, "cutoffCheck"),
                        FallbackCutoff: Text(payload, "fallbackCutoff"),
                        LastDespatch: Text(payload, "lastDespatch"),
                        CollectFrom: Text(payload, "collectFrom"),
                        CollectTo: Text(payload, "collectTo"),
                        DepotDeadline: Text(payload, "depotDeadline"),
                        LatestCollectionTime: Text(payload, "latestCollectionTime") ?? Text(payload, "wallBoardDeadline"),
                        WallBoardDeadline: Text(payload, "wallBoardDeadline") ?? Text(payload, "latestCollectionTime"),
                        FirstGeofenceResetsLiveEtos: Bool(payload, "firstGeofenceResetsLiveEtos") ?? true,
                        Source: row.Source,
                        ReviewedAtUtc: row.ReviewedAtUtc ?? row.ReceivedAtUtc));
                }
                else if (row.EntityType == SiteTimingRuleDetailType)
                {
                    var collectionContext = Text(payload, "collectionContext");
                    var collectionKey = Text(payload, "collectionKey");
                    var deliveryKey = Text(payload, "deliveryKey");
                    var routeCombination = Text(payload, "routeCombination");
                    if (!MatchesSite(siteTokens, collectionContext, collectionKey, routeCombination, row.IdempotencyKey)) continue;

                    routeTimings.Add(new SiteRouteTimingDto(
                        RouteCombination: routeCombination ?? collectionContext ?? site.Name,
                        CollectionContext: collectionContext,
                        CollectionKey: collectionKey,
                        DeliveryKey: deliveryKey,
                        PalletType: Text(payload, "palletType"),
                        LastDespatch: Text(payload, "lastDespatch"),
                        CollectFrom: Text(payload, "collectFrom"),
                        CollectTo: Text(payload, "collectTo"),
                        DepotDeadline: Text(payload, "depotDeadline"),
                        LatestCollectionTime: Text(payload, "latestCollectionTime") ?? Text(payload, "collectTo") ?? Text(payload, "collectFrom"),
                        FirstGeofenceResetsLiveEtos: Bool(payload, "firstGeofenceResetsLiveEtos") ?? true,
                        Source: row.Source,
                        ReviewedAtUtc: row.ReviewedAtUtc ?? row.ReceivedAtUtc));
                }
            }
            catch (JsonException) { }
        }

        var profile = new SiteTimingProfileDto(
            SiteId: site.Id,
            ExternalCode: site.ExternalCode,
            Name: site.Name,
            Aliases: site.Aliases,
            LatestCollectionTime: cutoffs.Select(item => item.LatestCollectionTime).Concat(routeTimings.Select(item => item.LatestCollectionTime)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            WallBoardDeadline: cutoffs.Select(item => item.WallBoardDeadline).Concat(routeTimings.Select(item => item.LatestCollectionTime)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            FirstGeofenceResetsLiveEtos: cutoffs.Any(item => item.FirstGeofenceResetsLiveEtos) || routeTimings.Any(item => item.FirstGeofenceResetsLiveEtos),
            Cutoffs: cutoffs
                .OrderBy(item => item.Plan)
                .ThenBy(item => item.PalletType)
                .ThenBy(item => item.LatestCollectionTime)
                .ToList(),
            RouteTimings: routeTimings
                .OrderBy(item => item.RouteCombination)
                .ThenBy(item => item.PalletType)
                .ToList());

        return Ok(profile);
    }

    [HttpPut("{id:guid}/aliases")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> UpdateAliases(Guid id, SiteAliasUpdateRequest request, CancellationToken ct)
    {
        var site = await db.Sites.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (site is null) return NotFound();

        await MasterDetailStore.EnrichSitesAsync(db, new[] { site }, ct);
        var before = site.Aliases;
        site.Aliases = CleanAliases(request.Aliases);

        var actor = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value;
        await PersistSiteDetailAsync(site, actor, ct);

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Site",
            EntityId = site.Id,
            Action = "AliasesUpdated",
            ChangedBy = actor ?? "unknown",
            ChangesJson = JsonSerializer.Serialize(new { before, after = site.Aliases })
        });
        await db.SaveChangesAsync(ct);

        // Alias changes are operational Master Data. Apply any unique exact alias match to
        // an unlinked Falcon geofence immediately rather than waiting for a re-import/restart.
        var geofenceLinksRepaired = await GeofenceSiteAliasRepair.EnsureAsync(db, ct);

        return Ok(new
        {
            site.Id,
            site.ExternalCode,
            site.Name,
            site.Aliases,
            geofenceLinksRepaired,
            masterDataAuthority = "SQL/TMS",
            sharePointSync = "disabled"
        });
    }

    private async Task PersistSiteDetailAsync(Site site, string? actor, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(site);
        var idempotencyKey = $"{SiteMasterDetailType}:{NormaliseKey(site.ExternalCode)}";
        var reviewedAt = DateTimeOffset.UtcNow;

        // Site aliases live in the audited master-detail register rather than the base Site
        // table. Update that row directly so repeated Site CRM saves cannot fail because an
        // immediately preceding Site edit has advanced the staged row-version.
        var updated = await db.StagedImports
            .Where(item => item.IdempotencyKey == idempotencyKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PayloadJson, payload)
                .SetProperty(item => item.Status, StagingStatus.Promoted)
                .SetProperty(item => item.Source, AliasEditorSource)
                .SetProperty(item => item.ReviewedAtUtc, reviewedAt)
                .SetProperty(item => item.ReviewedBy, actor)
                .SetProperty(item => item.ReviewNote, ReviewNote), ct);

        if (updated > 0) return;

        db.StagedImports.Add(new StagedImport
        {
            EntityType = SiteMasterDetailType,
            IdempotencyKey = idempotencyKey,
            PayloadJson = payload,
            Status = StagingStatus.Promoted,
            Source = AliasEditorSource,
            ReviewedAtUtc = reviewedAt,
            ReviewedBy = actor,
            ReviewNote = ReviewNote
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent first save may have inserted the same unique idempotency key.
            // Clear the failed insert and apply the authoritative alias payload to that row.
            db.ChangeTracker.Clear();
            await db.StagedImports
                .Where(item => item.IdempotencyKey == idempotencyKey)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.PayloadJson, payload)
                    .SetProperty(item => item.Status, StagingStatus.Promoted)
                    .SetProperty(item => item.Source, AliasEditorSource)
                    .SetProperty(item => item.ReviewedAtUtc, reviewedAt)
                    .SetProperty(item => item.ReviewedBy, actor)
                    .SetProperty(item => item.ReviewNote, ReviewNote), ct);
        }
    }

    private static IEnumerable<string?> BuildSiteTokens(Site site)
    {
        yield return site.ExternalCode;
        yield return site.Name;
        yield return site.DriverTextName;
        yield return site.CollectionAddress;
        foreach (var alias in SplitAliases(site.Aliases)) yield return alias;
    }

    private static bool MatchesSite(IEnumerable<string?> siteTokens, params string?[] values)
    {
        var tokenSet = siteTokens
            .Select(NormaliseKey)
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tokenSet.Count == 0) return false;

        foreach (var value in values)
        {
            var normalised = NormaliseKey(value);
            if (normalised.Length == 0) continue;
            if (tokenSet.Contains(normalised)) return true;
            if (tokenSet.Any(token => token.Length >= 4 && (normalised.Contains(token, StringComparison.OrdinalIgnoreCase) || token.Contains(normalised, StringComparison.OrdinalIgnoreCase)))) return true;
        }

        return false;
    }

    private static string NormaliseKey(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IEnumerable<string> SplitAliases(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (var alias in value.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!string.IsNullOrWhiteSpace(alias)) yield return alias.Trim();
    }

    private static string? Text(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!payload.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString();
            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return value.ToString();
        }
        return null;
    }

    private static bool? Bool(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!payload.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static string? CleanAliases(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var aliases = value
            .Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(alias => alias.Trim())
            .Where(alias => alias.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (aliases.Count == 0) return null;
        var joined = string.Join("; ", aliases);
        return joined.Length <= 500 ? joined : joined[..500];
    }
}

public sealed record SiteAliasUpdateRequest(string? Aliases);

public sealed record SiteTimingProfileDto(
    Guid SiteId,
    string ExternalCode,
    string Name,
    string? Aliases,
    string? LatestCollectionTime,
    string? WallBoardDeadline,
    bool FirstGeofenceResetsLiveEtos,
    IReadOnlyCollection<SiteCutoffTimingDto> Cutoffs,
    IReadOnlyCollection<SiteRouteTimingDto> RouteTimings);

public sealed record SiteCutoffTimingDto(
    string? SiteId,
    string? SiteName,
    bool MatchedLiveSite,
    string? Plan,
    string? Temperature,
    string? PalletType,
    string? StandardCutoff,
    string? ExtendedCutoff,
    string? CutoffCheck,
    string? FallbackCutoff,
    string? LastDespatch,
    string? CollectFrom,
    string? CollectTo,
    string? DepotDeadline,
    string? LatestCollectionTime,
    string? WallBoardDeadline,
    bool FirstGeofenceResetsLiveEtos,
    string? Source,
    DateTimeOffset ReviewedAtUtc);

public sealed record SiteRouteTimingDto(
    string RouteCombination,
    string? CollectionContext,
    string? CollectionKey,
    string? DeliveryKey,
    string? PalletType,
    string? LastDespatch,
    string? CollectFrom,
    string? CollectTo,
    string? DepotDeadline,
    string? LatestCollectionTime,
    bool FirstGeofenceResetsLiveEtos,
    string? Source,
    DateTimeOffset ReviewedAtUtc);
