using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/master-data-cleanup")]
[Authorize]
public sealed class MasterDataCleanupController(TmsDbContext db, IConfiguration configuration) : ControllerBase
{
    private const string DefaultBulkDeletePhrase = "DELETE";

    [HttpPost("{entity}/{id:guid}/archive"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> Archive(string entity, Guid id, CancellationToken ct) => SetActive(entity, id, false, ct);

    [HttpPost("{entity}/{id:guid}/restore"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> Restore(string entity, Guid id, CancellationToken ct) => SetActive(entity, id, true, ct);

    [HttpDelete("{entity}/{id:guid}"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Delete(string entity, Guid id, CancellationToken ct)
    {
        entity = Canonical(entity);
        if (entity.Length == 0) return Unsupported("deleted");

        var active = await ReadActive(entity, id, ct);
        if (active is null) return NotFound(new { code = "master_data_not_found", message = "This master-data record no longer exists." });
        if (active.Value)
            return Conflict(new { code = "archive_before_delete", message = "Archive this record first. Permanent delete is only available for an archived duplicate or incorrect master record." });

        List<object> usage;
        try { usage = await Usage(entity, id, ct); }
        catch (Exception ex)
        {
            return Conflict(new
            {
                code = "reference_check_unavailable",
                message = "The TMS could not safely prove that this record is unused, so it was not deleted. Archive it instead until the history check is available.",
                detail = ex.GetBaseException().Message
            });
        }

        if (usage.Count > 0)
            return Conflict(new
            {
                code = "master_data_in_use",
                message = $"This {Singular(entity)} is referenced by TMS history or live operational data and cannot be deleted. Keep it archived instead.",
                references = usage
            });

        var removed = await Remove(entity, id, false, ct);
        if (removed is null) return NotFound(new { code = "master_data_not_found", message = "This master-data record no longer exists." });

        var auditRecorded = await TryAudit(Title(Singular(entity)), id, "Deleted", removed.Value.Snapshot, null, ct);
        return Ok(new
        {
            deleted = true,
            entity,
            id,
            integrationMappingsRemoved = removed.Value.MappingsRemoved,
            auditRecorded,
            message = $"{Title(Singular(entity))} permanently deleted from the TMS master. Historical operational records were not removed."
        });
    }

    [HttpPost("{entity}/bulk-delete"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> BulkDelete(string entity, BulkMasterDataDeleteRequest request, CancellationToken ct)
    {
        entity = Canonical(entity);
        if (entity is not ("drivers" or "sites")) return Unsupported("bulk deleted");
        if (!AdminPhraseAccepted(request.AdminPassword))
            return Unauthorized(new
            {
                code = "admin_password_required",
                message = "Enter the master-data delete password before bulk deleting records."
            });

        var ids = request.Ids.Distinct().Take(250).ToList();
        if (ids.Count == 0) return BadRequest(new { code = "no_records_selected", message = "Select at least one row to delete." });

        Dictionary<Guid, List<object>> directUsage;
        List<string> recoveryRegisterPayloads;
        try
        {
            directUsage = ids.ToDictionary(id => id, _ => new List<object>());

            if (entity == "sites")
            {
                var geofenceCounts = await db.SiteGeofences.AsNoTracking()
                    .Where(x => x.SiteId.HasValue && ids.Contains(x.SiteId.Value))
                    .GroupBy(x => x.SiteId!.Value)
                    .Select(group => new { Id = group.Key, Count = group.Count() })
                    .ToListAsync(ct);

                foreach (var row in geofenceCounts)
                    directUsage[row.Id].Add(new { area = "Geofences", count = row.Count });
            }
            else
            {
                var runCounts = await db.Loads.AsNoTracking()
                    .Where(x => x.DriverId.HasValue && ids.Contains(x.DriverId.Value))
                    .GroupBy(x => x.DriverId!.Value)
                    .Select(group => new { Id = group.Key, Count = group.Count() })
                    .ToListAsync(ct);

                foreach (var row in runCounts)
                    directUsage[row.Id].Add(new { area = "Runs", count = row.Count });

                var statusCounts = await db.DriverStatusLogs.AsNoTracking()
                    .Where(x => x.DriverId.HasValue && ids.Contains(x.DriverId.Value))
                    .GroupBy(x => x.DriverId!.Value)
                    .Select(group => new { Id = group.Key, Count = group.Count() })
                    .ToListAsync(ct);

                foreach (var row in statusCounts)
                    directUsage[row.Id].Add(new { area = "Driver status history", count = row.Count });
            }

            var needsRegisterCheck = ids.Any(id => directUsage[id].Count == 0);
            recoveryRegisterPayloads = needsRegisterCheck
                ? await db.StagedImports.AsNoTracking()
                    .Where(x => x.EntityType.StartsWith("register:") && x.Status != StagingStatus.Rejected)
                    .Select(x => x.PayloadJson)
                    .ToListAsync(ct)
                : new List<string>();
        }
        catch (Exception ex)
        {
            return Conflict(new
            {
                code = "reference_check_unavailable",
                message = "The TMS could not safely prove that the selected records are unused, so nothing was deleted.",
                detail = ex.GetBaseException().Message
            });
        }

        var deleted = new List<object>();
        var blocked = new List<object>();
        var notFound = new List<Guid>();
        foreach (var id in ids)
        {
            var item = await Find(entity, id, ct);
            if (item is null)
            {
                notFound.Add(id);
                continue;
            }

            var forceSiteHistoryOverride = entity == "sites" && request.ForceHistoryOverride;
            if (forceSiteHistoryOverride && GetActive(item))
            {
                blocked.Add(new
                {
                    id,
                    label = Label(item),
                    reason = "Archive this site before using the force delete override."
                });
                continue;
            }

            var usage = directUsage[id];
            if (usage.Count == 0 && recoveryRegisterPayloads.Count > 0)
            {
                var token = id.ToString();
                var planningCount = recoveryRegisterPayloads.Count(payload =>
                    payload.Contains(token, StringComparison.OrdinalIgnoreCase));
                if (planningCount > 0)
                    usage.Add(new { area = "Planning register", count = planningCount });
            }

            if (usage.Count > 0 && !forceSiteHistoryOverride)
            {
                blocked.Add(new
                {
                    id,
                    label = Label(item),
                    reason = $"This {Singular(entity)} is referenced by TMS history or live operational data.",
                    references = usage
                });
                continue;
            }

            var removed = await Remove(entity, id, forceSiteHistoryOverride, ct);
            if (removed is null)
            {
                notFound.Add(id);
                continue;
            }

            var auditRecorded = await TryAudit(
                Title(Singular(entity)),
                id,
                removed.Value.GeofencesDetached > 0 ? "BulkForceDeletedGeofenceLinksDetached" : "BulkDeleted",
                removed.Value.Snapshot,
                null,
                ct);
            deleted.Add(new
            {
                id,
                label = Label(item),
                integrationMappingsRemoved = removed.Value.MappingsRemoved,
                geofenceLinksDetached = removed.Value.GeofencesDetached,
                auditRecorded
            });
        }

        return Ok(new
        {
            entity,
            requested = ids.Count,
            deleted = deleted.Count,
            blocked = blocked.Count,
            notFound = notFound.Count,
            deletedRows = deleted,
            blockedRows = blocked,
            notFoundRows = notFound,
            message = $"{deleted.Count} {Singular(entity)} record{(deleted.Count == 1 ? "" : "s")} permanently deleted. {blocked.Count} blocked by live/history references.{(entity == "sites" && request.ForceHistoryOverride ? " Linked geofences were detached; geofence visits and history were retained." : string.Empty)}"
        });
    }

    private async Task<IActionResult> SetActive(string entity, Guid id, bool active, CancellationToken ct)
    {
        entity = Canonical(entity);
        if (entity.Length == 0) return Unsupported(active ? "restored" : "archived");
        var item = await Find(entity, id, ct);
        if (item is null) return NotFound(new { code = "master_data_not_found", message = "This master-data record no longer exists." });
        if (GetActive(item) == active) return Ok(new { active, unchanged = true });

        var before = Snapshot(item);
        SetActiveValue(item, active);
        if (item is SiteGeofence geofence) geofence.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        var auditRecorded = await TryAudit(Title(Singular(entity)), id, active ? "Restored" : "Archived", before, Snapshot(item), ct);
        return Ok(new { active, entity, id, auditRecorded });
    }

    private async Task<object?> Find(string entity, Guid id, CancellationToken ct) => entity switch
    {
        "drivers" => await db.Drivers.FirstOrDefaultAsync(x => x.Id == id, ct),
        "vehicles" => await db.Vehicles.FirstOrDefaultAsync(x => x.Id == id, ct),
        "trailers" => await db.Trailers.FirstOrDefaultAsync(x => x.Id == id, ct),
        "sites" => await db.Sites.FirstOrDefaultAsync(x => x.Id == id, ct),
        "customers" => await db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct),
        "geofences" => await db.SiteGeofences.FirstOrDefaultAsync(x => x.Id == id, ct),
        _ => null
    };

    private async Task<bool?> ReadActive(string entity, Guid id, CancellationToken ct) => entity switch
    {
        "drivers" => await db.Drivers.AsNoTracking().Where(x => x.Id == id).Select(x => (bool?)x.Active).FirstOrDefaultAsync(ct),
        "vehicles" => await db.Vehicles.AsNoTracking().Where(x => x.Id == id).Select(x => (bool?)x.Active).FirstOrDefaultAsync(ct),
        "trailers" => await db.Trailers.AsNoTracking().Where(x => x.Id == id).Select(x => (bool?)x.Active).FirstOrDefaultAsync(ct),
        "sites" => await db.Sites.AsNoTracking().Where(x => x.Id == id).Select(x => (bool?)x.Active).FirstOrDefaultAsync(ct),
        "customers" => await db.Customers.AsNoTracking().Where(x => x.Id == id).Select(x => (bool?)x.Active).FirstOrDefaultAsync(ct),
        "geofences" => await db.SiteGeofences.AsNoTracking().Where(x => x.Id == id).Select(x => (bool?)x.Active).FirstOrDefaultAsync(ct),
        _ => null
    };

    private async Task<List<object>> Usage(string entity, Guid id, CancellationToken ct)
    {
        var result = new List<object>();
        void Add(string area, int count) { if (count > 0) result.Add(new { area, count }); }

        switch (entity)
        {
            case "vehicles":
                Add("Runs", await db.Loads.AsNoTracking().CountAsync(x => x.VehicleId == id, ct));
                Add("Geofence history", await db.GeofenceVisits.AsNoTracking().CountAsync(x => x.VehicleId == id, ct));
                break;
            case "drivers":
                Add("Runs", await db.Loads.AsNoTracking().CountAsync(x => x.DriverId == id, ct));
                Add("Driver status history", await db.DriverStatusLogs.AsNoTracking().CountAsync(x => x.DriverId == id, ct));
                break;
            case "trailers":
                Add("Runs", await db.Loads.AsNoTracking().CountAsync(x => x.TrailerId == id, ct));
                break;
            case "sites":
                Add("Geofences", await db.SiteGeofences.AsNoTracking().CountAsync(x => x.SiteId == id, ct));
                break;
            case "geofences":
                Add("Geofence visit history", await db.GeofenceVisits.AsNoTracking().CountAsync(x => x.GeofenceId == id, ct));
                break;
            case "customers":
            {
                var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                if (customer is null) return result;
                Add("Orders", await db.TransportOrders.AsNoTracking().CountAsync(x => x.CustomerCode == customer.Code, ct));
                Add("Customer contacts", await db.CustomerContacts.AsNoTracking().CountAsync(x => x.CustomerCode == customer.Code, ct));
                Add("Order / planning register", await db.StagedImports.AsNoTracking().CountAsync(x =>
                    (x.EntityType == "order" || x.EntityType == "register:order") &&
                    x.Status != StagingStatus.Rejected && x.PayloadJson.Contains(customer.Code), ct));
                return result;
            }
        }

        if (result.Count > 0) return result;

        Add("Planning register", await db.StagedImports.AsNoTracking().CountAsync(x =>
            x.EntityType.StartsWith("register:") && x.Status != StagingStatus.Rejected && x.PayloadJson.Contains(id.ToString()), ct));
        return result;
    }

    private async Task<(int MappingsRemoved, int GeofencesDetached, string Snapshot)?> Remove(string entity, Guid id, bool detachSiteGeofences, CancellationToken ct)
    {
        var item = await Find(entity, id, ct);
        if (item is null) return null;
        var snapshot = Snapshot(item);

        var mappings = await db.IntegrationMappings.Where(x => x.TmsEntityId == id).ToListAsync(ct);
        if (mappings.Count > 0) db.IntegrationMappings.RemoveRange(mappings);

        var geofencesDetached = 0;
        if (detachSiteGeofences && item is Site)
        {
            var linkedGeofences = await db.SiteGeofences.Where(x => x.SiteId == id).ToListAsync(ct);
            foreach (var geofence in linkedGeofences)
            {
                // Keep the geofence and its visit history, but remove the deleted site's master link.
                geofence.SiteId = null;
                geofence.SiteNumber = null;
                geofence.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
            geofencesDetached = linkedGeofences.Count;
        }

        switch (item)
        {
            case Driver value: db.Drivers.Remove(value); break;
            case Vehicle value: db.Vehicles.Remove(value); break;
            case Trailer value: db.Trailers.Remove(value); break;
            case Site value: db.Sites.Remove(value); break;
            case Customer value: db.Customers.Remove(value); break;
            case SiteGeofence value: db.SiteGeofences.Remove(value); break;
            default: return null;
        }

        await db.SaveChangesAsync(ct);
        return (mappings.Count, geofencesDetached, snapshot);
    }

    private async Task<bool> TryAudit(string entityType, Guid entityId, string action, string before, string? after, CancellationToken ct)
    {
        try
        {
            db.MasterDataAudits.Add(BuildAudit(entityType, entityId, action, before, after));
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private MasterDataAudit BuildAudit(string entityType, Guid entityId, string action, string before, string? after)
    {
        using var beforeDoc = JsonDocument.Parse(before);
        using var afterDoc = after is null ? null : JsonDocument.Parse(after);
        return new MasterDataAudit
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            ChangesJson = JsonSerializer.Serialize(new { before = beforeDoc.RootElement.Clone(), after = afterDoc?.RootElement.Clone() }),
            ChangedBy = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "unknown"
        };
    }

    private IActionResult Unsupported(string action) => BadRequest(new
    {
        code = "unsupported_master_entity",
        message = $"This master-data type cannot be {action} from the cleanup screen."
    });

    private static bool GetActive(object item) => item switch
    {
        Driver x => x.Active, Vehicle x => x.Active, Trailer x => x.Active,
        Site x => x.Active, Customer x => x.Active, SiteGeofence x => x.Active, _ => false
    };

    private static void SetActiveValue(object item, bool active)
    {
        switch (item)
        {
            case Driver x: x.Active = active; break;
            case Vehicle x: x.Active = active; break;
            case Trailer x: x.Active = active; break;
            case Site x: x.Active = active; break;
            case Customer x: x.Active = active; break;
            case SiteGeofence x: x.Active = active; break;
        }
    }

    private bool AdminPhraseAccepted(string? value)
    {
        var configured = configuration["MasterDataCleanup:BulkDeletePassword"] ?? Environment.GetEnvironmentVariable("MasterDataCleanup__BulkDeletePassword");
        var expected = string.IsNullOrWhiteSpace(configured) ? DefaultBulkDeletePhrase : configured.Trim();
        return string.Equals(value?.Trim(), expected, StringComparison.Ordinal);
    }

    private static string Label(object item) => item switch
    {
        Driver x => $"{x.DisplayName} ({x.EmployeeNumber})",
        Site x => $"{x.Name} ({x.ExternalCode})",
        Vehicle x => x.Registration,
        Trailer x => x.TrailerNumber,
        Customer x => $"{x.Name} ({x.Code})",
        SiteGeofence x => x.Name,
        _ => item.GetType().Name
    };

    private static string Canonical(string value) => value.Trim().ToLowerInvariant() switch
    {
        "driver" or "drivers" => "drivers", "vehicle" or "vehicles" => "vehicles",
        "trailer" or "trailers" => "trailers", "site" or "sites" => "sites",
        "customer" or "customers" => "customers", "geofence" or "geofences" => "geofences", _ => string.Empty
    };
    private static string Singular(string entity) => entity.EndsWith('s') ? entity[..^1] : entity;
    private static string Title(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
    private static string Snapshot<T>(T value) => JsonSerializer.Serialize(value);
}

public sealed record BulkMasterDataDeleteRequest(
    IReadOnlyList<Guid> Ids,
    string? AdminPassword,
    bool ForceHistoryOverride = false);
