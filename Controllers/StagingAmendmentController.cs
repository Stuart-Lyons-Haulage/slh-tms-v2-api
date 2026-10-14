using System.Text.Json;
using System.Text.Json.Nodes;
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

        var po = FirstText(request.Payload, "poNumber", "customerPo", "customerRef", "poRef", "orderReference", "reference");
        var customer = FirstText(request.Payload, "customerCode", "customer", "customer_supplier");
        var date = FirstText(request.Payload, "collectionDate", "collectDate", "pickupDate", "date");
        if (string.IsNullOrWhiteSpace(po) || string.IsNullOrWhiteSpace(customer) || !DateOnly.TryParse(date, out _))
            return BadRequest(new { message = "Order requires poNumber/reference, customerCode and a valid collectionDate before it can be saved." });

        var amendedPayload = JsonNode.Parse(request.Payload.GetRawText())?.AsObject();
        if (amendedPayload is null) return BadRequest(new { message = "Order payload must be a JSON object." });
        amendedPayload["poNumber"] = po;
        amendedPayload["customerCode"] = customer;
        amendedPayload["collectionDate"] = date;
        StagedOrderPayloadAmendment.Apply(amendedPayload);

        var previousStatus = item.Status;
        item.PayloadJson = amendedPayload.ToJsonString();
        await NwfBookingReservationSync.UpsertAsync(db, item, JsonSerializer.SerializeToElement(amendedPayload), User.Identity?.Name, ct);
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
                await EnsureSiteAliasMappingAsync(selected, importedName, null, ct);
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

    /// <summary>
    /// Match an unknown collection or destination value to an existing canonical site.
    /// The alias is persisted in both the audited Site master and the intake mapping
    /// register so future mailbox parses resolve it before staging.
    /// </summary>
    [HttpPost("{id:guid}/match-site"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> MatchSite(Guid id, StagedSiteMatchRequest request, CancellationToken ct)
    {
        var (item, payload, error) = await LoadPendingOrderAsync(id, ct);
        if (error is not null) return error;
        if (!TryField(request.Field, out var field)) return BadRequest(new { message = "Field must be collection or destination." });
        if (request.SiteId == Guid.Empty) return BadRequest(new { message = "A canonical site is required." });

        var selected = await db.Sites.SingleOrDefaultAsync(site => site.Id == request.SiteId && site.Active, ct);
        if (selected is null) return NotFound(new { message = "The selected active Site Master record no longer exists." });

        var alias = ClipRequired(request.Alias ?? PayloadSiteValue(payload!, field) ?? string.Empty, 200);
        if (string.IsNullOrWhiteSpace(alias)) return BadRequest(new { message = "Enter the source site name or address to store as an alias." });

        var conflict = await FindAliasOwnerAsync(alias, selected.Id, ct);
        if (conflict is not null)
            return Conflict(new { message = $"{alias} is already matched to {conflict.Name} ({conflict.ExternalCode}).", siteId = conflict.Id });

        await MasterDetailStore.EnrichSitesAsync(db, new[] { selected }, ct);
        selected.Aliases = AppendAlias(selected.Aliases, alias);
        await EnsureSiteAliasMappingAsync(selected, alias, PayloadCustomer(payload!), ct);
        ApplySiteToPayload(payload!, field, selected.Name, selected.CollectionAddress);
        await SaveSiteMappingAsync(item!, payload!, selected, $"Matched {field} alias '{alias}' to {selected.Name}.", ct);

        return Ok(new { siteId = selected.Id, siteName = selected.Name, alias, field, updated = true });
    }

    /// <summary>
    /// Create a new canonical site from an order-review exception and apply it to the
    /// current staged order. Creation is deliberately explicit and duplicate-checked;
    /// the parser will never create master data on its own.
    /// </summary>
    [HttpPost("{id:guid}/create-site"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> CreateSite(Guid id, CreateStagedSiteRequest request, CancellationToken ct)
    {
        var (item, payload, error) = await LoadPendingOrderAsync(id, ct);
        if (error is not null) return error;
        if (!TryField(request.Field, out var field)) return BadRequest(new { message = "Field must be collection or destination." });

        var name = ClipRequired(request.Name ?? string.Empty, 200);
        var address = Clip(request.Address, 500);
        var customer = Clip(PayloadCustomer(payload!) ?? request.CustomerCode, 40)?.ToUpperInvariant();
        var alias = Clip(request.Alias ?? name, 200);
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "A site name is required." });
        if (string.IsNullOrWhiteSpace(address)) return BadRequest(new { message = "A site address is required." });

        var duplicate = await FindDuplicateSiteAsync(customer, name, address, ct);
        if (duplicate is not null)
            return Conflict(new { message = $"A matching active site already exists: {duplicate.Name} ({duplicate.ExternalCode}). Use Match as alias instead.", siteId = duplicate.Id });
        var aliasOwner = await FindAliasOwnerAsync(alias, Guid.Empty, ct);
        if (aliasOwner is not null)
            return Conflict(new { message = $"The alias '{alias}' is already matched to {aliasOwner.Name} ({aliasOwner.ExternalCode}). Use Match as alias instead.", siteId = aliasOwner.Id });

        var externalCode = await NewExternalCodeAsync(customer, name, ct);
        var site = new Site
        {
            ExternalCode = externalCode,
            CustomerCode = customer,
            Name = name,
            DriverTextName = name,
            CollectionAddress = address,
            Aliases = string.Equals(alias, name, StringComparison.OrdinalIgnoreCase) ? null : alias,
            Active = true
        };
        db.Sites.Add(site);
        await EnsureSiteAliasMappingAsync(site, alias, customer, ct);
        ApplySiteToPayload(payload!, field, site.Name, site.CollectionAddress);
        await SaveSiteMappingAsync(item!, payload!, site, $"Created {field} site {site.Name} from order review mapping exception.", ct);

        return Ok(new { siteId = site.Id, siteName = site.Name, externalCode, alias, field, created = true });
    }

    private async Task<(StagedImport? Item, JsonObject? Payload, IActionResult? Error)> LoadPendingOrderAsync(Guid id, CancellationToken ct)
    {
        var item = await db.StagedImports.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (item is null) return (null, null, NotFound());
        if (item.Status != StagingStatus.PendingReview || !string.Equals(item.EntityType, "order", StringComparison.OrdinalIgnoreCase))
            return (null, null, BadRequest(new { message = "Only pending staged orders can be mapped." }));
        try
        {
            var payload = JsonNode.Parse(item.PayloadJson)?.AsObject();
            return payload is null ? (null, null, BadRequest(new { message = "The staged order payload could not be read." })) : (item, payload, null);
        }
        catch (JsonException) { return (null, null, BadRequest(new { message = "The staged order payload could not be read." })); }
    }

    private async Task SaveSiteMappingAsync(StagedImport item, JsonObject payload, Site site, string note, CancellationToken ct)
    {
        item.PayloadJson = payload.ToJsonString();
        item.ReviewNote = string.Join(" | ", new[] { item.ReviewNote, note }.Where(value => !string.IsNullOrWhiteSpace(value)));
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Site",
            EntityId = site.Id,
            Action = "OrderReviewMapping",
            ChangedBy = User.Identity?.Name ?? User.FindFirst("oid")?.Value ?? "authorised user",
            ChangesJson = JsonSerializer.Serialize(new { site.Id, site.ExternalCode, site.Name, site.Aliases, note })
        });
        db.StagedImportEvents.Add(StagingAudit.Create(item, "SiteMappingUpdated", item.Status, note, User.Identity?.Name ?? User.FindFirst("oid")?.Value));
        await db.SaveChangesAsync(ct);
        await MasterDetailStore.SaveAsync(db, "site", site.ExternalCode, JsonSerializer.Serialize(site), "Order Review mapping action", User.Identity?.Name, ct);
    }

    private async Task EnsureSiteAliasMappingAsync(Site site, string alias, string? customerCode, CancellationToken ct)
    {
        var normalised = IntakeMappingService.Normalize(alias);
        if (normalised.Length == 0) return;
        var existing = await db.IntegrationMappings.Where(mapping => mapping.Active && mapping.TmsEntityType == "Site" && mapping.MappingKind == "SiteAlias" && (mapping.NormalizedExternalValue == normalised || mapping.ExternalKey == alias)).ToListAsync(ct);
        var other = existing.FirstOrDefault(mapping => mapping.TmsEntityId != site.Id);
        if (other is not null)
            throw new InvalidOperationException($"The alias '{alias}' is already assigned to another site.");
        if (existing.Any(mapping => mapping.TmsEntityId == site.Id)) return;
        db.IntegrationMappings.Add(new IntegrationMapping
        {
            Provider = "InfoMailbox",
            ExternalKey = alias.Trim(),
            ExternalLabel = alias.Trim(),
            TmsEntityType = "Site",
            TmsEntityId = site.Id,
            MappingKind = "SiteAlias",
            NormalizedExternalValue = normalised,
            SenderPattern = string.IsNullOrWhiteSpace(customerCode) ? null : customerCode.Trim(),
            ConfidenceThreshold = 0.98m,
            Notes = "Created from an explicit Order Review mapping action.",
            UpdatedBy = User.Identity?.Name ?? User.FindFirst("oid")?.Value
        });
    }

    private async Task<Site?> FindAliasOwnerAsync(string alias, Guid selectedId, CancellationToken ct)
    {
        var normalised = IntakeMappingService.Normalize(alias);
        if (normalised.Length == 0) return null;
        var mapping = await db.IntegrationMappings.AsNoTracking().Where(item => item.Active && item.TmsEntityType == "Site" && item.MappingKind == "SiteAlias" && item.NormalizedExternalValue == normalised && item.TmsEntityId != selectedId).FirstOrDefaultAsync(ct);
        return mapping is null ? null : await db.Sites.AsNoTracking().FirstOrDefaultAsync(site => site.Id == mapping.TmsEntityId && site.Active, ct);
    }

    private async Task<Site?> FindDuplicateSiteAsync(string? customer, string name, string address, CancellationToken ct)
    {
        var nameKey = IntakeMappingService.Normalize(name);
        var addressKey = IntakeMappingService.Normalize(address);
        var sites = await db.Sites.AsNoTracking().Where(site => site.Active && (string.IsNullOrWhiteSpace(customer) || site.CustomerCode == customer || site.CustomerCode == null)).ToListAsync(ct);
        return sites.FirstOrDefault(site => IntakeMappingService.Normalize(site.Name) == nameKey ||
            IntakeMappingService.Normalize(site.DriverTextName) == nameKey ||
            (!string.IsNullOrWhiteSpace(site.CollectionAddress) && IntakeMappingService.Normalize(site.CollectionAddress) == addressKey));
    }

    private async Task<string> NewExternalCodeAsync(string? customer, string name, CancellationToken ct)
    {
        var seed = IntakeMappingService.Normalize($"{customer}-{name}");
        seed = seed.Length > 26 ? seed[..26] : seed;
        var code = $"INT-{seed}";
        var suffix = 1;
        while (await db.Sites.AnyAsync(site => site.ExternalCode == code, ct)) code = $"INT-{seed}-{suffix++}";
        return code.Length > 40 ? code[..40] : code;
    }

    private static bool TryField(string? value, out string field)
    {
        field = (value ?? string.Empty).Trim().ToLowerInvariant();
        return field is "collection" or "destination";
    }

    private static string? PayloadCustomer(JsonObject payload) => Text(payload, "customerCode", "customer", "customer_supplier");
    private static string? PayloadSiteValue(JsonObject payload, string field) => field == "collection" ? Text(payload, "sellerName", "collectionSite") : Text(payload, "stallNumber", "deliverySite", "deliveryLocation", "destination");

    private static void ApplySiteToPayload(JsonObject payload, string field, string siteName, string? address)
    {
        if (field == "collection")
        {
            payload["sellerName"] = siteName;
            payload["collectionSite"] = siteName;
        }
        else
        {
            payload["stallNumber"] = siteName;
            payload["deliverySite"] = siteName;
            payload["deliveryLocation"] = siteName;
            payload["destination"] = siteName;
        }
        if (!string.IsNullOrWhiteSpace(address)) payload["deliveryAddress"] = address;
    }

    private static string AppendAlias(string? existing, string alias) => string.Join("; ", (existing ?? string.Empty).Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(alias.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string? Clip(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
    private static string ClipRequired(string value, int maxLength) => value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];

    private static string? Text(JsonObject payload, params string[] names) => names.Select(name => payload.FirstOrDefault(property => string.Equals(new string(property.Key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()), new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()), StringComparison.OrdinalIgnoreCase)).Value?.ToString()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

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

    private static string? FirstText(JsonElement payload, params string[] names) =>
        names.Select(name => Text(payload, name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public sealed record StagedPayloadAmendment(JsonElement Payload, string? Note);
public sealed record DeliverySiteMatchRequest(Guid SiteId, Guid? GeofenceId = null);
public sealed record StagedSiteMatchRequest(Guid SiteId, string? Alias, string? Field);
public sealed record CreateStagedSiteRequest(string? Field, string? Name, string? Address, string? Alias, string? CustomerCode);
