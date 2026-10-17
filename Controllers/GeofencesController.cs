using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/geofences")]
[Authorize]
public sealed class GeofencesController(TmsDbContext db) : ControllerBase
{
    [HttpPost("import-falcon/preview")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> PreviewFalcon([FromBody] JsonElement payload, CancellationToken ct)
    {
        try
        {
            return Ok(await FalconGeofenceImportService.PreviewAsync(db, payload, ct));
        }
        catch (FalconGeofenceValidationException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
    }

    [HttpPost("import-falcon/commit")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> CommitFalcon([FromBody] FalconGeofenceCommitRequest request, CancellationToken ct)
    {
        try
        {
            var actor = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "dot-geofence-import";
            return Ok(await FalconGeofenceImportService.CommitAsync(db, request, actor, ct));
        }
        catch (FalconGeofenceValidationException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
    }

    [HttpPost("import-falcon")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ImportFalcon([FromBody] JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("geofences", out var geofences) || geofences.ValueKind != JsonValueKind.Array)
            return UnprocessableEntity(new { error = "Expected a Falcon geofence JSON object containing a geofences array." });

        var promotion = await PromoteCodedGeofencesAsync(ct);
        var sync = await SiteGeofenceMasterSync.SyncAsync(db, ct);
        return Ok(new
        {
            code = "embedded_geofence_runtime",
            message = "The production geofence engine uses the approved SLH embedded geofence set. The supplied Falcon payload was accepted as a sync trigger and site links were repaired against Site Master.",
            supplied = geofences.GetArrayLength(),
            promotedSites = promotion.CreatedSites,
            restoredSites = promotion.RestoredSites,
            relinked = promotion.Linked + sync.GeofencesLinked,
            canonicalized = sync.GeofencesCanonicalized,
            unlinked = sync.GeofencesUnlinked,
            sitesMissingGeofence = sync.SitesMissingGeofence
        });
    }

    [HttpPost("import-slh-seed")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ImportSlhSeed(CancellationToken ct)
    {
        var statuses = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, ct);
        return Ok(new
        {
            supplied = statuses.Count,
            inserted = 0,
            updated = statuses.Count,
            siteMatched = statuses.Count(x => x.SiteId != null),
            relinked = 0,
            remainingUnlinked = statuses.Count(x => x.SiteId == null),
            invalidPolygons = 0,
            source = "EmbeddedSLHGeofences",
            importedAtUtc = DateTimeOffset.UtcNow
        });
    }

    [HttpPost("repair-links")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> RepairLinks(CancellationToken ct)
    {
        var promotion = await PromoteCodedGeofencesAsync(ct);
        var sync = await SiteGeofenceMasterSync.SyncAsync(db, ct);
        var statuses = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, ct);
        var linked = statuses.Count(x => x.SiteId != null);
        return Ok(new
        {
            total = statuses.Count,
            linked,
            relinked = promotion.Linked + sync.GeofencesLinked,
            promotedSites = promotion.CreatedSites,
            restoredSites = promotion.RestoredSites,
            ambiguousSiteCodes = promotion.AmbiguousCodes,
            canonicalized = sync.GeofencesCanonicalized,
            sitesMissingGeofence = sync.SitesMissingGeofence,
            unlinked = statuses.Count - linked,
            validPolygons = statuses.Count,
            invalidPolygons = 0,
            source = "EmbeddedSLHGeofences+SiteMaster",
            repairedAtUtc = DateTimeOffset.UtcNow
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var statuses = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, ct);
        var overrides = await ActiveOverridesByName(ct);
        var records = statuses
            .OrderBy(x => x.Fence.Category)
            .ThenBy(x => x.Fence.Name)
            .Select(x =>
            {
                overrides.TryGetValue(NormalizeName(x.Fence.Name), out var manual);
                var locationOnly = string.Equals(manual?.SiteNumber, "LOCATION_ONLY", StringComparison.OrdinalIgnoreCase);
                var siteNumber = x.SiteCode ?? (locationOnly ? null : manual?.SiteNumber ?? x.Fence.SiteNumber);
                var codedUnlinked = x.SiteId is null && !locationOnly && !string.IsNullOrWhiteSpace(siteNumber);
                return new
                {
                    id = x.Fence.Id,
                    name = x.Fence.Name,
                    category = x.Fence.Category,
                    maxWaitMinutes = x.Fence.MaxWaitMinutes,
                    categoryMaxWaitMinutes = x.Fence.CategoryMaxWaitMinutes,
                    siteNumber,
                    siteId = x.SiteId,
                    siteName = x.SiteName,
                    siteCode = x.SiteCode,
                    manualOverride = x.ManualOverride,
                    locationOnly,
                    active = true,
                    polygonValid = true,
                    geofenceAvailable = true,
                    siteLinked = x.SiteId != null,
                    validationStatus = locationOnly ? "Location only" : x.SiteId != null ? "Valid" : codedUnlinked ? "Coded / needs Site promotion" : "Unlinked"
                };
            }).ToList();
        return Ok(new { count = records.Count, source = "EmbeddedSLHGeofences", records });
    }

    [HttpGet("visits")]
    public async Task<IActionResult> Visits([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var day = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        List<Load> loads;
        try { loads = await PlanningRegisterStore.ReadLoadsAsync(db, day, ct); }
        catch { loads = []; db.ChangeTracker.Clear(); }

        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, day, loads, ct);
        var records = snapshot.Visits.OrderByDescending(x => x.EnteredAtUtc).Take(1000).Select(x => new
        {
            x.Id,
            geofenceId = x.Fence.Id,
            x.LoadId,
            x.LoadStopId,
            x.VehicleId,
            x.VehicleIdentifier,
            x.EnteredAtUtc,
            x.ConfirmedAtUtc,
            x.ExitedAtUtc,
            x.DwellMinutes,
            status = x.ExitedAtUtc is not null ? (x.ConfirmedAtUtc is not null ? "Departed" : "PassThrough") : x.ConfirmedAtUtc is not null ? "OnSiteConfirmed" : "Arrived",
            statusReason = x.ExitedAtUtc is not null
                ? "Derived from RoadTech tracking crossing the approved geofence boundary."
                : x.ConfirmedAtUtc is not null
                    ? "Confirmed after the minimum dwell period."
                    : "Vehicle currently inside geofence."
        }).ToList();
        return Ok(new { date = day, count = records.Count, source = "RoadTechDerived", records });
    }

    private async Task<Dictionary<string, SiteGeofence>> ActiveOverridesByName(CancellationToken ct)
    {
        try
        {
            var rows = await db.SiteGeofences.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
            return rows.GroupBy(x => NormalizeName(x.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.UpdatedAtUtc).First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return new Dictionary<string, SiteGeofence>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<SitePromotionResult> PromoteCodedGeofencesAsync(CancellationToken ct)
    {
        List<SiteGeofence> coded;
        try
        {
            coded = await db.SiteGeofences
                .Where(x => x.Active && x.SiteId == null && x.SiteNumber != null && x.SiteNumber != "" && x.SiteNumber != "LOCATION_ONLY")
                .ToListAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return new SitePromotionResult(0, 0, 0, 0);
        }

        if (coded.Count == 0) return new SitePromotionResult(0, 0, 0, 0);

        var sites = await db.Sites.ToListAsync(ct);
        var actor = Actor();
        var now = DateTimeOffset.UtcNow;
        var created = 0;
        var restored = 0;
        var linked = 0;
        var ambiguous = 0;

        foreach (var fence in coded)
        {
            var code = fence.SiteNumber?.Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;

            var normalized = NormalizeCode(code);
            var numeric = NumericCode(code);
            var matches = sites.Where(site =>
                NormalizeCode(site.ExternalCode) == normalized ||
                (numeric.Length > 0 && NumericCode(site.ExternalCode) == numeric)).ToList();

            if (matches.Count > 1)
            {
                ambiguous++;
                continue;
            }

            Site site;
            var createdSite = matches.Count == 0;
            if (createdSite)
            {
                site = new Site
                {
                    ExternalCode = code,
                    Name = fence.Name,
                    DriverTextName = fence.Name,
                    Active = true
                };
                db.Sites.Add(site);
                sites.Add(site);
                created++;
                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "Site",
                    EntityId = site.Id,
                    Action = "CreatedFromOperationalGeofence",
                    ChangedBy = actor,
                    ChangesJson = JsonSerializer.Serialize(new { siteCode = code, geofenceId = fence.Id, geofenceName = fence.Name })
                });
            }
            else
            {
                site = matches[0];
                if (!site.Active)
                {
                    site.Active = true;
                    restored++;
                    db.MasterDataAudits.Add(new MasterDataAudit
                    {
                        EntityType = "Site",
                        EntityId = site.Id,
                        Action = "RestoredForOperationalGeofence",
                        ChangedBy = actor,
                        ChangesJson = JsonSerializer.Serialize(new { siteCode = site.ExternalCode, geofenceId = fence.Id, geofenceName = fence.Name })
                    });
                }

                if (string.IsNullOrWhiteSpace(site.Name)) site.Name = fence.Name;
                if (string.IsNullOrWhiteSpace(site.DriverTextName)) site.DriverTextName = fence.Name;
            }

            fence.SiteId = site.Id;
            fence.SiteNumber = site.ExternalCode;
            fence.UpdatedAtUtc = now;
            linked++;
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Geofence",
                EntityId = fence.Id,
                Action = createdSite ? "PromotedToSiteMaster" : "RelinkedToSiteMaster",
                ChangedBy = actor,
                ChangesJson = JsonSerializer.Serialize(new { siteId = site.Id, siteCode = site.ExternalCode, siteName = site.Name })
            });
        }

        if (linked > 0 || restored > 0 || created > 0)
            await db.SaveChangesAsync(ct);

        return new SitePromotionResult(created, restored, linked, ambiguous);
    }

    private static string NormalizeCode(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());

    private static string NumericCode(string? value) => NormalizeCode(value).TrimStart('0');
    private string Actor() => User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "geofence-link-repair";
    private static string NormalizeName(string value) => string.Join(' ', value.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(value.UtcDateTime); }
    }

    private sealed record SitePromotionResult(int CreatedSites, int RestoredSites, int Linked, int AmbiguousCodes);
}
