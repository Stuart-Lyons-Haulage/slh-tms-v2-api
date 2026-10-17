using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/master-data")]
[Authorize]
public sealed class MasterDataRebuildController(TmsDbContext db) : ControllerBase
{
    [HttpPost("rebuild-reviewed-register"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> RebuildReviewedRegister(ReviewedMasterRebuildRequest request, CancellationToken ct)
    {
        if (request.Payload.MasterSites.Count == 0)
            return BadRequest(new { code = "empty_master_sites", message = "The reviewed import pack does not contain any master sites." });

        var now = DateTimeOffset.UtcNow;
        var actor = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "unknown";

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var archivedSites = 0;
        if (request.DeleteExisting.Sites)
        {
            var activeSites = await db.Sites.Where(site => site.Active).ToListAsync(ct);
            foreach (var site in activeSites) site.Active = false;
            archivedSites = activeSites.Count;
        }

        var archivedGeofences = 0;
        if (request.DeleteExisting.Geofences)
        {
            var activeGeofences = await db.SiteGeofences.Where(geofence => geofence.Active).ToListAsync(ct);
            foreach (var geofence in activeGeofences)
            {
                geofence.Active = false;
                geofence.UpdatedAtUtc = now;
            }
            archivedGeofences = activeGeofences.Count;
        }

        if (request.DeleteExisting.SiteAliases)
        {
            var aliasRows = await db.StagedImports
                .Where(row => row.EntityType == "masterdetail:site" && row.Status == StagingStatus.Promoted)
                .ToListAsync(ct);
            foreach (var row in aliasRows)
            {
                row.Status = StagingStatus.Archived;
                row.ReviewedAtUtc = now;
                row.ReviewedBy = actor;
                row.ReviewNote = "Archived by reviewed CRM master-data rebuild.";
            }
        }

        var existingSites = await db.Sites.ToDictionaryAsync(site => site.ExternalCode, StringComparer.OrdinalIgnoreCase, ct);
        var siteIdsByExternalCode = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var sitesUpserted = 0;

        foreach (var row in request.Payload.MasterSites)
        {
            var externalCode = Clean(row.ExternalCode) ?? Clean(row.SiteId);
            var displayName = Clean(row.PlannerDisplayName) ?? Clean(row.CanonicalSiteName);
            if (string.IsNullOrWhiteSpace(externalCode) || string.IsNullOrWhiteSpace(displayName)) continue;

            if (!existingSites.TryGetValue(externalCode, out var site))
            {
                site = new Site { ExternalCode = externalCode, Name = displayName };
                db.Sites.Add(site);
                existingSites[externalCode] = site;
            }

            site.Name = displayName;
            site.DriverTextName = Clean(row.CanonicalSiteName) ?? displayName;
            site.CollectionAddress = FirstNonEmpty(row.AddressSummary, row.Postcode);
            site.CollectionInstructions = Clean(row.StandardNotes);
            site.MapLink = Clean(row.MapLink);
            site.Active = row.Active ?? true;
            siteIdsByExternalCode[externalCode] = site.Id;
            sitesUpserted++;
        }

        await db.SaveChangesAsync(ct);

        var aliasesBySite = request.Payload.SiteAliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias.SiteId) && !string.IsNullOrWhiteSpace(alias.AliasName))
            .GroupBy(alias => alias.SiteId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group
                .Select(alias => alias.AliasName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value)
                .ToList(), StringComparer.OrdinalIgnoreCase);

        var detailsUpserted = 0;
        foreach (var site in await db.Sites.Where(site => site.Active).ToListAsync(ct))
        {
            aliasesBySite.TryGetValue(site.ExternalCode, out var aliases);
            var details = JsonSerializer.Serialize(new
            {
                externalCode = site.ExternalCode,
                siteCode = site.ExternalCode,
                aliases = aliases is { Count: > 0 } ? string.Join("; ", aliases) : null,
                source = "Reviewed CRM Master Sites 2026-08-25"
            });
            await UpsertSiteDetail(site.ExternalCode, details, actor, now, ct);
            detailsUpserted++;
        }

        var existingGeofences = await db.SiteGeofences.ToDictionaryAsync(geofence => geofence.NormalizedName, StringComparer.OrdinalIgnoreCase, ct);
        var linkedGeofences = 0;
        foreach (var row in request.Payload.SiteGeofences)
        {
            if (await UpsertGeofence(row, siteIdsByExternalCode, existingGeofences, now, ct)) linkedGeofences++;
        }

        var locationOnlyGeofences = 0;
        foreach (var row in request.Payload.GeofenceLocationsOnly)
        {
            if (await UpsertGeofence(row, siteIdsByExternalCode, existingGeofences, now, ct)) locationOnlyGeofences++;
        }

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "MasterRegister",
            EntityId = Guid.NewGuid(),
            Action = "ReviewedRebuild",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new
            {
                request.DeleteExisting,
                archivedSites,
                archivedGeofences,
                sitesUpserted,
                detailsUpserted,
                linkedGeofences,
                locationOnlyGeofences,
                request.Payload.Counts
            })
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Ok(new
        {
            archivedSites,
            archivedGeofences,
            sitesUpserted,
            siteDetailsUpserted = detailsUpserted,
            linkedGeofences,
            locationOnlyGeofences,
            message = "Reviewed CRM master register rebuilt. Existing live master records were archived before the reviewed active set was applied."
        });
    }

    private async Task UpsertSiteDetail(string externalCode, string payloadJson, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var key = $"masterdetail:site:{NormalizeKey(externalCode)}";
        var row = await db.StagedImports.SingleOrDefaultAsync(item => item.IdempotencyKey == key, ct);
        if (row is null)
        {
            row = new StagedImport
            {
                EntityType = "masterdetail:site",
                IdempotencyKey = key,
                PayloadJson = payloadJson,
                Source = "Reviewed CRM Master Sites 2026-08-25"
            };
            db.StagedImports.Add(row);
        }

        row.PayloadJson = payloadJson;
        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = now;
        row.ReviewedBy = actor;
        row.ReviewNote = "Reviewed CRM master-data rebuild.";
    }

    private Task<bool> UpsertGeofence(ReviewedGeofence row, Dictionary<string, Guid> siteIdsByExternalCode, Dictionary<string, SiteGeofence> existingGeofences, DateTimeOffset now, CancellationToken ct)
    {
        var name = Clean(row.DotName);
        var polygonJson = Clean(row.PolygonJson);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(polygonJson) || polygonJson == "[]") return Task.FromResult(false);

        var normalizedName = NormalizeName(name);
        if (!existingGeofences.TryGetValue(normalizedName, out var geofence))
        {
            geofence = new SiteGeofence { Name = name, NormalizedName = normalizedName, PolygonJson = polygonJson };
            db.SiteGeofences.Add(geofence);
            existingGeofences[normalizedName] = geofence;
        }

        geofence.Name = name;
        geofence.NormalizedName = normalizedName;
        geofence.Category = Clean(row.Category);
        geofence.CategoryMaxWaitMinutes = ToInt(row.CategoryMaxWaitTime);
        geofence.MaxWaitMinutes = ToInt(row.MaxWaitTime);
        geofence.PendingEntryMinutes = Math.Max(0, ToInt(row.PendingEntryMinutes) ?? 0);
        geofence.PendingExitMinutes = Math.Max(0, ToInt(row.PendingExitMinutes) ?? 0);
        geofence.SiteNumber = Clean(row.SiteNo);
        geofence.SiteId = Clean(row.SiteId) is { } siteCode && siteIdsByExternalCode.TryGetValue(siteCode, out var siteId) ? siteId : null;
        geofence.PolygonJson = polygonJson;
        geofence.Active = row.Active ?? true;
        geofence.UpdatedAtUtc = now;

        return Task.FromResult(true);
    }

    private static string? FirstNonEmpty(params string?[] values) => values.Select(Clean).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeKey(string value) => string.Concat(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit));
    private static string NormalizeName(string value) => string.Join(' ', value.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static int? ToInt(JsonElement? value)
    {
        if (value is null) return null;
        return value.Value.ValueKind switch
        {
            JsonValueKind.Number when value.Value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.Value.GetString(), out var number) => number,
            _ => null
        };
    }
}

public sealed record ReviewedMasterRebuildRequest(
    [property: JsonPropertyName("deleteExisting")] ReviewedDeleteExisting DeleteExisting,
    [property: JsonPropertyName("payload")] ReviewedMasterPayload Payload);
public sealed record ReviewedDeleteExisting(
    [property: JsonPropertyName("sites")] bool Sites,
    [property: JsonPropertyName("siteAliases")] bool SiteAliases,
    [property: JsonPropertyName("geofences")] bool Geofences);
public sealed record ReviewedMasterPayload(
    [property: JsonPropertyName("counts")] Dictionary<string, int>? Counts,
    [property: JsonPropertyName("master_sites")] List<ReviewedMasterSite> MasterSites,
    [property: JsonPropertyName("site_aliases")] List<ReviewedSiteAlias> SiteAliases,
    [property: JsonPropertyName("site_geofences")] List<ReviewedGeofence> SiteGeofences,
    [property: JsonPropertyName("geofence_locations_only")] List<ReviewedGeofence> GeofenceLocationsOnly);
public sealed record ReviewedMasterSite(
    [property: JsonPropertyName("site_id")] string? SiteId,
    [property: JsonPropertyName("external_code")] string? ExternalCode,
    [property: JsonPropertyName("planner_display_name")] string? PlannerDisplayName,
    [property: JsonPropertyName("canonical_site_name")] string? CanonicalSiteName,
    [property: JsonPropertyName("postcode")] string? Postcode,
    [property: JsonPropertyName("address_summary")] string? AddressSummary,
    [property: JsonPropertyName("map_link")] string? MapLink,
    [property: JsonPropertyName("standard_notes")] string? StandardNotes,
    [property: JsonPropertyName("active")] bool? Active);
public sealed record ReviewedSiteAlias(
    [property: JsonPropertyName("site_id")] string SiteId,
    [property: JsonPropertyName("alias_name")] string AliasName,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("alias_type")] string? AliasType,
    [property: JsonPropertyName("active")] bool? Active,
    [property: JsonPropertyName("notes")] string? Notes);
public sealed record ReviewedGeofence(
    [property: JsonPropertyName("geofence_id")] string? GeofenceId,
    [property: JsonPropertyName("dot_name")] string? DotName,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("source_file")] string? SourceFile,
    [property: JsonPropertyName("site_no")] string? SiteNo,
    [property: JsonPropertyName("category_max_wait_time")] JsonElement? CategoryMaxWaitTime,
    [property: JsonPropertyName("max_wait_time")] JsonElement? MaxWaitTime,
    [property: JsonPropertyName("pending_entry_minutes")] JsonElement? PendingEntryMinutes,
    [property: JsonPropertyName("pending_exit_minutes")] JsonElement? PendingExitMinutes,
    [property: JsonPropertyName("polygon_json")] string? PolygonJson,
    [property: JsonPropertyName("site_id")] string? SiteId,
    [property: JsonPropertyName("active")] bool? Active);
