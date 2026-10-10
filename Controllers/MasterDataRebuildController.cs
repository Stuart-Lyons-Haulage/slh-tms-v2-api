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
                sitesUpserted,
                detailsUpserted,
                request.Payload.Counts
            })
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Ok(new
        {
            archivedSites,
            sitesUpserted,
            siteDetailsUpserted = detailsUpserted,
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

    private static string? FirstNonEmpty(params string?[] values) => values.Select(Clean).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeKey(string value) => string.Concat(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit));
}

public sealed record ReviewedMasterRebuildRequest(
    [property: JsonPropertyName("deleteExisting")] ReviewedDeleteExisting DeleteExisting,
    [property: JsonPropertyName("payload")] ReviewedMasterPayload Payload);
public sealed record ReviewedDeleteExisting(
    [property: JsonPropertyName("sites")] bool Sites,
    [property: JsonPropertyName("siteAliases")] bool SiteAliases);
public sealed record ReviewedMasterPayload(
    [property: JsonPropertyName("counts")] Dictionary<string, int>? Counts,
    [property: JsonPropertyName("master_sites")] List<ReviewedMasterSite> MasterSites,
    [property: JsonPropertyName("site_aliases")] List<ReviewedSiteAlias> SiteAliases);
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
