using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/staging")]
[Authorize]
public sealed class StagingAmendmentController(TmsDbContext db) : ControllerBase
{
    [HttpPut("{id:guid}/payload"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Amend(
        Guid id,
        [FromBody] StagedPayloadAmendment request,
        CancellationToken ct)
    {
        var item = await db.StagedImports.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (item is null) return NotFound();
        if (item.Status != StagingStatus.PendingReview)
            return BadRequest(new { message = "Only pending staged records can be amended." });
        if (!string.Equals(item.EntityType, "order", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "This amendment endpoint is currently limited to staged orders." });
        if (request.Payload.ValueKind != JsonValueKind.Object)
            return BadRequest(new { message = "Order payload must be a JSON object." });

        var po = Text(request.Payload, "poNumber");
        var customer = Text(request.Payload, "customerCode");
        var date = Text(request.Payload, "collectionDate");
        if (string.IsNullOrWhiteSpace(po) || string.IsNullOrWhiteSpace(customer) || !DateOnly.TryParse(date, out _))
            return BadRequest(new { message = "Order requires poNumber/reference, customerCode and a valid collectionDate before it can be saved." });

        var previousStatus = item.Status;
        item.PayloadJson = request.Payload.GetRawText();
        item.ReviewNote = string.Join(" | ", new[]
        {
            item.ReviewNote,
            request.Note,
            $"Pending payload amended by {User.Identity?.Name ?? "authorised user"} at {DateTimeOffset.UtcNow:O}."
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        db.StagedImportEvents.Add(StagingAudit.Create(
            item,
            "Amended",
            previousStatus,
            request.Note,
            User.Identity?.Name ?? User.FindFirst("oid")?.Value));
        await db.SaveChangesAsync(ct);
        return Ok(item);
    }

    [HttpPost("{id:guid}/confirm-delivery-site"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> ConfirmDeliverySite(Guid id, DeliverySiteMatchRequest request, CancellationToken ct)
    {
        var item = await db.StagedImports.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (item is null) return NotFound();
        if (item.Status != StagingStatus.PendingReview || !string.Equals(item.EntityType, "order", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Only pending staged orders can be matched to a master delivery site." });

        JsonDocument document;
        try { document = JsonDocument.Parse(item.PayloadJson); }
        catch (JsonException) { return BadRequest(new { message = "The staged order payload could not be read." }); }
        using (document)
        {
            var importedName = DeliveryName(document.RootElement);
            if (string.IsNullOrWhiteSpace(importedName))
                return BadRequest(new { message = "This order has no delivery site name to add to the Site Master." });

            var sites = await db.Sites.AsNoTracking().Where(site => site.Active).ToListAsync(ct);
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
            var selected = sites.SingleOrDefault(site => site.Id == request.SiteId);
            if (selected is null) return NotFound(new { message = "The selected active Site Master record no longer exists." });

            var existingMatch = sites.FirstOrDefault(site => site.Id != selected.Id && SiteNames(site).Any(name => SameName(name, importedName)));
            if (existingMatch is not null)
                return Conflict(new { message = $"{importedName} is already matched to {existingMatch.Name} ({existingMatch.ExternalCode}). Choose that site or correct the import name." });

            var aliases = SiteNames(selected)
                .Append(importedName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var changed = !SiteNames(selected).Any(name => SameName(name, importedName));
            selected.Aliases = string.Join("; ", aliases.Where(name => !SameName(name, selected.Name) && !SameName(name, selected.DriverTextName) && !SameName(name, selected.ExternalCode)));

            if (changed)
            {
                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "Site",
                    EntityId = selected.Id,
                    Action = "DeliveryImportAliasAdded",
                    ChangedBy = User.Identity?.Name ?? User.FindFirst("oid")?.Value ?? "authorised user",
                    ChangesJson = JsonSerializer.Serialize(new { importedDeliveryName = importedName, aliases = selected.Aliases })
                });
                await MasterDetailStore.SaveAsync(db, "site", selected.ExternalCode, JsonSerializer.Serialize(selected), "Order Review delivery-site match", User.Identity?.Name, ct);
            }

            SiteGeofence? geofence = null;
            Guid? previousGeofenceSiteId = null;
            if (request.GeofenceId is Guid geofenceId)
            {
                geofence = await db.SiteGeofences.FirstOrDefaultAsync(fence => fence.Id == geofenceId && fence.Active, ct);
                if (geofence is null) return NotFound(new { message = "The selected active geofence no longer exists." });
                previousGeofenceSiteId = geofence.SiteId;
                geofence.SiteId = selected.Id;
                geofence.SiteNumber = selected.ExternalCode;
                geofence.UpdatedAtUtc = DateTimeOffset.UtcNow;
                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "Geofence",
                    EntityId = geofence.Id,
                    Action = "DeliveryImportSiteConfirmed",
                    ChangedBy = User.Identity?.Name ?? User.FindFirst("oid")?.Value ?? "authorised user",
                    ChangesJson = JsonSerializer.Serialize(new
                    {
                        geofence = geofence.Name,
                        importedDeliveryName = importedName,
                        previousSiteId = previousGeofenceSiteId,
                        siteId = selected.Id,
                        siteCode = selected.ExternalCode
                    })
                });
            }

            item.ReviewNote = string.Join(" | ", new[]
            {
                item.ReviewNote,
                $"Delivery site '{importedName}' confirmed as {selected.Name} ({selected.ExternalCode})."
            }.Where(note => !string.IsNullOrWhiteSpace(note)));
            db.StagedImportEvents.Add(StagingAudit.Create(
                item,
                "DeliverySiteMasterMatched",
                item.Status,
                item.ReviewNote,
                User.Identity?.Name ?? User.FindFirst("oid")?.Value));
            await db.SaveChangesAsync(ct);
            return Ok(new
            {
                siteId = selected.Id,
                siteName = selected.Name,
                siteCode = selected.ExternalCode,
                importedDeliveryName = importedName,
                aliasAdded = changed,
                geofenceLinked = geofence is not null,
                geofenceName = geofence?.Name,
                previousGeofenceSiteId
            });
        }
    }

    private static string? DeliveryName(JsonElement payload) =>
        Text(payload, "deliverySite") ?? Text(payload, "deliveryLocation") ?? Text(payload, "stallNumber") ?? Text(payload, "destination");

    private static IEnumerable<string?> SiteNames(Site site)
    {
        yield return site.Name;
        yield return site.DriverTextName;
        yield return site.ExternalCode;
        foreach (var alias in (site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return alias;
    }

    private static bool SameName(string? left, string? right) => Normalise(left) == Normalise(right) && Normalise(left).Length > 0;
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? Text(JsonElement payload, string name)
    {
        foreach (var property in payload.EnumerateObject())
        {
            var key = new string(property.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            var wanted = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            if (key != wanted) continue;
            return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : property.Value.GetRawText();
        }
        return null;
    }
}

public sealed record StagedPayloadAmendment(JsonElement Payload, string? Note);
public sealed record DeliverySiteMatchRequest(Guid SiteId, Guid? GeofenceId = null);
