using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/operational-master-data")]
[Authorize]
public sealed class OperationalMasterDataController(TmsDbContext db) : ControllerBase
{
    [HttpGet("drivers/search")]
    public async Task<IActionResult> SearchDrivers([FromQuery] string? q, [FromQuery] bool includeInactive, CancellationToken ct)
    {
        q = (q ?? string.Empty).Trim();
        var query = db.Drivers.AsNoTracking().Where(x => includeInactive || x.Active);
        if (q.Length > 0)
            query = query.Where(x => x.DisplayName.Contains(q) || x.EmployeeNumber.Contains(q) || (x.TachoName != null && x.TachoName.Contains(q)));

        var result = await query.OrderBy(x => x.DisplayName).Take(500)
            .Select(x => new
            {
                x.Id, x.DisplayName, x.EmployeeNumber, x.TachoName, x.MobileNumber,
                x.DriverType, x.DriverGroup, x.Skills, x.Active,
                x.TachoMasterDriverId, x.TachoCardNumber,
                x.TachoDriveAvailableTodayMinutes, x.TachoDriveAvailableWeekMinutes,
                x.TachoWorkAvailableWeekMinutes, x.LastTachoSyncUtc
            }).ToListAsync(ct);
        // The endpoint is a driver register: hide office references and collapse
        // historical duplicate rows by Tacho member, card, or employee reference.
        var filtered = result.Where(x => !DriverPopulationRules.IsOfficeReference(x.EmployeeNumber) &&
            (!string.IsNullOrWhiteSpace(x.TachoCardNumber) ||
             DriverPopulationRules.HasDriverRole(x.DriverType) || DriverPopulationRules.HasDriverRole(x.DriverGroup) ||
             string.Equals(x.DriverType?.Trim(), "Agency", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(x => !string.IsNullOrWhiteSpace(x.TachoMasterDriverId) ? $"member:{x.TachoMasterDriverId}" :
                          !string.IsNullOrWhiteSpace(x.TachoCardNumber) ? $"card:{x.TachoCardNumber}" : $"employee:{x.EmployeeNumber}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(x => x.DisplayName)
            .Take(50);
        return Ok(filtered);
    }

    [HttpGet("drivers/{id:guid}")]
    public async Task<IActionResult> GetDriver(Guid id, CancellationToken ct)
    {
        var driver = await db.Drivers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return driver is null ? NotFound() : Ok(driver);
    }

    [HttpPut("drivers/{id:guid}"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> UpdateDriver(Guid id, DriverUpdateRequest request, CancellationToken ct)
    {
        var driver = await db.Drivers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (driver is null) return NotFound();
        var before = Snapshot(driver);
        driver.DisplayName = CleanRequired(request.DisplayName, driver.DisplayName);
        driver.EmployeeNumber = CleanRequired(request.EmployeeNumber, driver.EmployeeNumber);
        driver.TachoName = Clean(request.TachoName);
        driver.MobileNumber = Clean(request.MobileNumber);
        driver.DriverType = Clean(request.DriverType);
        driver.DriverGroup = Clean(request.DriverGroup);
        driver.Skills = Clean(request.Skills);
        await Audit("Driver", id, "Updated", before, Snapshot(driver), ct);
        return Ok(driver);
    }

    [HttpPost("drivers/{id:guid}/archive"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> ArchiveDriver(Guid id, CancellationToken ct) => SetDriverActive(id, false, ct);

    [HttpPost("drivers/{id:guid}/restore"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> RestoreDriver(Guid id, CancellationToken ct) => SetDriverActive(id, true, ct);

    [HttpGet("vehicles/search")]
    public async Task<IActionResult> SearchVehicles([FromQuery] string? q, [FromQuery] bool includeInactive, CancellationToken ct)
    {
        q = NormalizeReg(q ?? string.Empty);
        var query = db.Vehicles.AsNoTracking().Where(x => includeInactive || x.Active);
        if (q.Length > 0)
            query = query.Where(x => x.Registration.Replace(" ", "").Contains(q) || (x.Abbreviation != null && x.Abbreviation.Contains(q)) || (x.FleetNumber != null && x.FleetNumber.Contains(q)));

        var vehicles = await query.OrderBy(x => x.Registration).Take(50).ToListAsync(ct);
        var identifiers = vehicles.SelectMany(v => new[] { NormalizeReg(v.Registration), NormalizeReg(v.Abbreviation ?? string.Empty) }).Where(x => x.Length > 0).Distinct().ToList();
        var live = await db.VehicleLiveStatuses.AsNoTracking().Where(x => identifiers.Contains(x.VehicleIdentifier.Replace(" ", "").ToUpper())).ToListAsync(ct);
        var result = vehicles.Select(v =>
        {
            var status = live.OrderByDescending(x => x.LastEventTimeUtc).FirstOrDefault(x => NormalizeReg(x.VehicleIdentifier) == NormalizeReg(v.Registration) || NormalizeReg(x.VehicleIdentifier) == NormalizeReg(v.Abbreviation ?? string.Empty));
            return new
            {
                v.Id, v.Registration, v.VIN, v.OwnerType, v.VehicleSite, v.FleetNumber, v.Abbreviation, v.Transmission,
                v.DvsCompliant, v.FuelProvider, v.CabMobile, v.FuelPin, v.ShellCard, v.BpRedCard, v.BpPlainCard,
                v.FuelPinSecretName, v.FuelCardLastFour, v.MOTExpiry, v.TachoCalibrationExpiry, v.VehicleTestExpiry,
                v.Notes, v.FleetioId, v.FleetioName, v.FleetioStatus, v.Active,
                lastLocation = status is null ? null : new { status.Latitude, status.Longitude, status.LastEventTimeUtc, status.IsMoving, status.LastKnownStatus }
            };
        });
        return Ok(result);
    }

    [HttpGet("vehicles/{id:guid}")]
    public async Task<IActionResult> GetVehicle(Guid id, CancellationToken ct)
    {
        var vehicle = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (vehicle is null) return NotFound();
        var reg = NormalizeReg(vehicle.Registration);
        var abbreviation = NormalizeReg(vehicle.Abbreviation ?? string.Empty);
        var live = await db.VehicleLiveStatuses.AsNoTracking()
            .Where(x => x.VehicleIdentifier.Replace(" ", "").ToUpper() == reg || (abbreviation.Length > 0 && x.VehicleIdentifier.Replace(" ", "").ToUpper() == abbreviation))
            .OrderByDescending(x => x.LastEventTimeUtc).FirstOrDefaultAsync(ct);
        var lastLoad = await db.Loads.AsNoTracking().Where(x => x.VehicleId == id).OrderByDescending(x => x.PlanningDate).ThenByDescending(x => x.CreatedAtUtc).Select(x => new { x.Id, x.Reference, x.PlanningDate, x.DriverId, x.TrailerId, x.Status }).FirstOrDefaultAsync(ct);
        return Ok(new { vehicle, live, lastLoad });
    }

    [HttpPut("vehicles/{id:guid}"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> UpdateVehicle(Guid id, VehicleUpdateRequest request, CancellationToken ct)
    {
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (vehicle is null) return NotFound();
        var before = Snapshot(vehicle);
        vehicle.Registration = CleanRequired(request.Registration, vehicle.Registration).ToUpperInvariant();
        vehicle.VIN = Clean(request.VIN);
        vehicle.OwnerType = Clean(request.OwnerType);
        vehicle.VehicleSite = Clean(request.VehicleSite);
        vehicle.FleetNumber = Clean(request.FleetNumber);
        vehicle.Abbreviation = Clean(request.Abbreviation)?.ToUpperInvariant();
        vehicle.Transmission = Clean(request.Transmission);
        vehicle.DvsCompliant = request.DvsCompliant;
        vehicle.FuelProvider = Clean(request.FuelProvider);
        vehicle.CabMobile = Clean(request.CabMobile);
        vehicle.FuelPin = Clean(request.FuelPin);
        vehicle.ShellCard = Clean(request.ShellCard);
        vehicle.BpRedCard = Clean(request.BpRedCard);
        vehicle.BpPlainCard = Clean(request.BpPlainCard);
        vehicle.FuelPinSecretName = Clean(request.FuelPinSecretName);
        vehicle.FuelCardLastFour = Clean(request.FuelCardLastFour);
        vehicle.MOTExpiry = request.MOTExpiry;
        vehicle.TachoCalibrationExpiry = request.TachoCalibrationExpiry;
        vehicle.VehicleTestExpiry = request.VehicleTestExpiry;
        vehicle.Notes = Clean(request.Notes);
        vehicle.FleetioId = Clean(request.FleetioId);
        vehicle.FleetioName = Clean(request.FleetioName);
        vehicle.FleetioStatus = Clean(request.FleetioStatus);
        await Audit("Vehicle", id, "Updated", before, Snapshot(vehicle), ct);
        return Ok(vehicle);
    }

    [HttpPost("vehicles/{id:guid}/archive"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> ArchiveVehicle(Guid id, CancellationToken ct) => SetVehicleActive(id, false, ct);

    [HttpPost("vehicles/{id:guid}/restore"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> RestoreVehicle(Guid id, CancellationToken ct) => SetVehicleActive(id, true, ct);

    [HttpGet("trailers/search")]
    public async Task<IActionResult> SearchTrailers([FromQuery] string? q, [FromQuery] bool includeInactive, CancellationToken ct)
    {
        q = (q ?? string.Empty).Trim();
        var query = db.Trailers.AsNoTracking().Where(x => includeInactive || x.Active);
        if (q.Length > 0) query = query.Where(x => x.TrailerNumber.Contains(q) || (x.Type != null && x.Type.Contains(q)));
        var rows = await query.OrderBy(x => x.TrailerNumber).Take(50).ToListAsync(ct);
        await MasterDetailStore.EnrichTrailersAsync(db, rows, ct);
        return Ok(rows);
    }

    [HttpPut("trailers/{id:guid}"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> UpdateTrailer(Guid id, TrailerUpdateRequest request, CancellationToken ct)
    {
        var trailer = await db.Trailers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (trailer is null) return NotFound();
        var before = Snapshot(trailer);
        trailer.TrailerNumber = CleanRequired(request.TrailerNumber, trailer.TrailerNumber);
        trailer.Type = Clean(request.Type);
        trailer.StandardCapacity = request.StandardCapacity;
        trailer.EuroCapacity = request.EuroCapacity;
        trailer.Notes = Clean(request.Notes);
        await Audit("Trailer", id, "Updated", before, Snapshot(trailer), ct);
        await MasterDetailStore.SaveAsync(db, "trailer", trailer.TrailerNumber, Snapshot(trailer), "SLH operational trailer editor", User.Identity?.Name, ct);
        return Ok(trailer);
    }

    [HttpPost("trailers/{id:guid}/archive"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> ArchiveTrailer(Guid id, CancellationToken ct) => SetActive(db.Trailers, id, false, "Trailer", x => x.Active, (x, v) => x.Active = v, ct);

    [HttpPost("trailers/{id:guid}/restore"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> RestoreTrailer(Guid id, CancellationToken ct) => SetActive(db.Trailers, id, true, "Trailer", x => x.Active, (x, v) => x.Active = v, ct);

    [HttpGet("sites/search")]
    public async Task<IActionResult> SearchSites([FromQuery] string? q, [FromQuery] bool includeInactive, CancellationToken ct)
    {
        q = (q ?? string.Empty).Trim();
        var query = db.Sites.AsNoTracking().Where(x => includeInactive || x.Active);
        if (q.Length > 0) query = query.Where(x => x.Name.Contains(q) || x.ExternalCode.Contains(q) || (x.DriverTextName != null && x.DriverTextName.Contains(q)));
        var rows = await query.OrderBy(x => x.Name).Take(100).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, rows, ct);
        return Ok(rows);
    }

    [HttpPut("sites/{id:guid}"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> UpdateSite(Guid id, SiteUpdateRequest request, CancellationToken ct)
    {
        var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (site is null) return NotFound();
        await MasterDetailStore.EnrichSitesAsync(db, new[] { site }, ct);
        var before = Snapshot(site);
        site.ExternalCode = CleanRequired(request.ExternalCode, site.ExternalCode);
        site.CustomerCode = Clean(request.CustomerCode);
        site.Name = CleanRequired(request.Name, site.Name);
        site.DriverTextName = Clean(request.DriverTextName);
        site.CollectionAddress = Clean(request.CollectionAddress);
        site.CollectionInstructions = Clean(request.CollectionInstructions);
        site.MapLink = Clean(request.MapLink);
        site.Latitude = request.Latitude;
        site.Longitude = request.Longitude;
        site.Aliases = Clean(request.Aliases);
        site.CustomField1 = Clean(request.CustomField1);
        site.CustomField2 = Clean(request.CustomField2);
        site.CustomField3 = Clean(request.CustomField3);
        site.RoadrunnerCode = Clean(request.RoadrunnerCode);
        site.RoadrunnerProfileJson = Clean(request.RoadrunnerProfileJson);
        site.OperationalRegion = Clean(request.OperationalRegion);
        await Audit("Site", id, "Updated", before, Snapshot(site), ct);
        await MasterDetailStore.SaveAsync(db, "site", site.ExternalCode, Snapshot(site), "SLH operational site editor", User.Identity?.Name, ct);
        return Ok(site);
    }

    [HttpPost("sites/{id:guid}/archive"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> ArchiveSite(Guid id, CancellationToken ct) => SetActive(db.Sites, id, false, "Site", x => x.Active, (x, v) => x.Active = v, ct);

    [HttpPost("sites/{id:guid}/restore"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> RestoreSite(Guid id, CancellationToken ct) => SetActive(db.Sites, id, true, "Site", x => x.Active, (x, v) => x.Active = v, ct);

    [HttpGet("customers/search")]
    public async Task<IActionResult> SearchCustomers([FromQuery] string? q, [FromQuery] bool includeInactive, CancellationToken ct)
    {
        q = (q ?? string.Empty).Trim();
        var query = db.Customers.AsNoTracking().Where(x => includeInactive || x.Active);
        if (q.Length > 0) query = query.Where(x => x.Name.Contains(q) || x.Code.Contains(q));
        return Ok(await query.OrderBy(x => x.Name).Take(100).ToListAsync(ct));
    }

    [HttpPut("customers/{id:guid}"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> UpdateCustomer(Guid id, CustomerUpdateRequest request, CancellationToken ct)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (customer is null) return NotFound();
        var before = Snapshot(customer);
        customer.Code = CleanRequired(request.Code, customer.Code);
        customer.Name = CleanRequired(request.Name, customer.Name);
        customer.TradingName = Clean(request.TradingName);
        customer.AccountOwner = Clean(request.AccountOwner);
        customer.ServiceNotes = Clean(request.ServiceNotes);
        customer.DefaultSiteCode = Clean(request.DefaultSiteCode);
        await Audit("Customer", id, "Updated", before, Snapshot(customer), ct);
        return Ok(customer);
    }

    [HttpPost("customers/{id:guid}/archive"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> ArchiveCustomer(Guid id, CancellationToken ct) => SetActive(db.Customers, id, false, "Customer", x => x.Active, (x, v) => x.Active = v, ct);

    [HttpPost("customers/{id:guid}/restore"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> RestoreCustomer(Guid id, CancellationToken ct) => SetActive(db.Customers, id, true, "Customer", x => x.Active, (x, v) => x.Active = v, ct);

    [HttpGet("geofences/search")]
    public async Task<IActionResult> SearchGeofences([FromQuery] string? q, [FromQuery] bool includeInactive, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        q = (q ?? string.Empty).Trim();
        take = Math.Clamp(take, 1, 5000);
        var stored = await db.SiteGeofences.AsNoTracking()
            .Where(x => includeInactive || x.Active)
            .ToListAsync(ct);

        // Some approved RoadTech geofences exist in the runtime seed before a user links
        // them to a Site Master record. Include them here so they can be selected directly.
        var storedIds = stored.Select(x => x.Id).ToHashSet();
        var seeded = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => !storedIds.Contains(fence.Id))
            .Select(fence => new SiteGeofence
            {
                Id = fence.Id,
                Name = fence.Name,
                NormalizedName = NormalizeName(fence.Name),
                Category = fence.Category,
                CategoryMaxWaitMinutes = fence.CategoryMaxWaitMinutes,
                MaxWaitMinutes = fence.MaxWaitMinutes,
                PendingEntryMinutes = fence.PendingEntryMinutes,
                PendingExitMinutes = fence.PendingExitMinutes,
                SiteNumber = fence.SiteNumber,
                PolygonJson = PolygonJson(fence),
                Active = true
            });

        var rows = stored.Concat(seeded);
        if (q.Length > 0)
            rows = rows.Where(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (x.SiteNumber?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (x.Category?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        return Ok(rows.OrderBy(x => x.Name).Take(take).ToList());
    }

    [HttpPut("geofences/{id:guid}"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> UpdateGeofence(Guid id, GeofenceUpdateRequest request, CancellationToken ct)
    {
        var name = CleanRequired(request.Name, EmbeddedGeofenceEngine.ApprovedFences.FirstOrDefault(x => x.Id == id)?.Name ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return NotFound();
        var item = await ResolveEditableGeofence(id, name, request.PolygonJson, ct);
        var before = Snapshot(item);
        item.Name = name;
        item.NormalizedName = NormalizeName(item.Name);
        item.Category = Clean(request.Category);
        item.CategoryMaxWaitMinutes = request.CategoryMaxWaitMinutes;
        item.MaxWaitMinutes = request.MaxWaitMinutes;
        item.PendingEntryMinutes = Math.Max(0, request.PendingEntryMinutes);
        item.PendingExitMinutes = Math.Max(0, request.PendingExitMinutes);
        ApplySiteLink(item, request.SiteNumber, request.SiteId, request.LocationOnly);
        if (!string.IsNullOrWhiteSpace(request.PolygonJson)) item.PolygonJson = request.PolygonJson.Trim();
        item.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await Audit("Geofence", id, "Updated", before, Snapshot(item), ct);
        return Ok(await GeofenceResponse(item, ct));
    }

    [HttpPost("geofences/{id:guid}/sync-site"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> SyncGeofenceSite(Guid id, GeofenceSiteSyncRequest request, CancellationToken ct)
    {
        var embedded = EmbeddedGeofenceEngine.ApprovedFences.FirstOrDefault(x => x.Id == id);
        var name = CleanRequired(request.Name, embedded?.Name ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return NotFound();
        var item = await ResolveEditableGeofence(id, name, request.PolygonJson, ct);
        var before = Snapshot(item);
        ApplySiteLink(item, request.SiteNumber, request.SiteId, request.LocationOnly);
        item.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await Audit("Geofence", id, request.LocationOnly == true ? "MarkedLocationOnly" : "SyncedSiteLink", before, Snapshot(item), ct);
        return Ok(await GeofenceResponse(item, ct));
    }

    [HttpPost("geofences/{id:guid}/archive"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> ArchiveGeofence(Guid id, CancellationToken ct) => SetActive(db.SiteGeofences, id, false, "Geofence", x => x.Active, (x, v) => { x.Active = v; x.UpdatedAtUtc = DateTimeOffset.UtcNow; }, ct);

    [HttpPost("geofences/{id:guid}/restore"), Authorize(Policy = "TmsApprove")]
    public Task<IActionResult> RestoreGeofence(Guid id, CancellationToken ct) => SetActive(db.SiteGeofences, id, true, "Geofence", x => x.Active, (x, v) => { x.Active = v; x.UpdatedAtUtc = DateTimeOffset.UtcNow; }, ct);

    [HttpGet("audit/{entityType}/{entityId:guid}")]
    public async Task<IActionResult> AuditHistory(string entityType, Guid entityId, CancellationToken ct)
        => Ok(await db.MasterDataAudits.AsNoTracking().Where(x => x.EntityType == entityType && x.EntityId == entityId).OrderByDescending(x => x.ChangedAtUtc).Take(200).ToListAsync(ct));

    private async Task<IActionResult> SetDriverActive(Guid id, bool active, CancellationToken ct)
    {
        var driver = await db.Drivers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (driver is null) return NotFound();
        if (driver.Active == active) return Ok(new { active });
        var before = Snapshot(driver);
        driver.Active = active;
        await Audit("Driver", id, active ? "Restored" : "Archived", before, Snapshot(driver), ct);
        return Ok(new { active });
    }

    private async Task<IActionResult> SetVehicleActive(Guid id, bool active, CancellationToken ct)
    {
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (vehicle is null) return NotFound();
        if (vehicle.Active == active) return Ok(new { active });
        var before = Snapshot(vehicle);
        vehicle.Active = active;
        await Audit("Vehicle", id, active ? "Restored" : "Archived", before, Snapshot(vehicle), ct);
        return Ok(new { active });
    }

    private async Task<IActionResult> SetActive<TEntity>(DbSet<TEntity> set, Guid id, bool active, string entityType, Func<TEntity, bool> activeGetter, Action<TEntity, bool> activeSetter, CancellationToken ct) where TEntity : class
    {
        var item = await set.FindAsync(new object?[] { id }, ct);
        if (item is null) return NotFound();
        if (activeGetter(item) == active) return Ok(new { active });
        var before = Snapshot(item);
        activeSetter(item, active);
        await Audit(entityType, id, active ? "Restored" : "Archived", before, Snapshot(item), ct);
        return Ok(new { active });
    }

    private async Task Audit(string entityType, Guid entityId, string action, string before, string after, CancellationToken ct)
    {
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            ChangesJson = JsonSerializer.Serialize(new { before = JsonDocument.Parse(before).RootElement, after = JsonDocument.Parse(after).RootElement }),
            ChangedBy = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "unknown"
        });
        await db.SaveChangesAsync(ct);
    }

    private static string Snapshot<T>(T value) => JsonSerializer.Serialize(value);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string CleanRequired(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string NormalizeReg(string value) => value.Replace(" ", string.Empty).Replace("-", string.Empty).Trim().ToUpperInvariant();
    private static string NormalizeName(string value) => string.Join(' ', value.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    private static string NormalizeCode(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string NumericCode(string? value) => NormalizeCode(value).TrimStart('0');

    private async Task<SiteGeofence> ResolveEditableGeofence(Guid id, string name, string? polygonJson, CancellationToken ct)
    {
        var normalizedName = NormalizeName(name);
        var item = await db.SiteGeofences.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? await db.SiteGeofences.FirstOrDefaultAsync(x => x.NormalizedName == normalizedName, ct);
        if (item is not null) return item;

        var embedded = EmbeddedGeofenceEngine.ApprovedFences.FirstOrDefault(x => x.Id == id || NormalizeName(x.Name) == normalizedName);
        item = new SiteGeofence
        {
            Id = id,
            Name = name,
            NormalizedName = normalizedName,
            Category = embedded?.Category,
            CategoryMaxWaitMinutes = embedded?.CategoryMaxWaitMinutes,
            MaxWaitMinutes = embedded?.MaxWaitMinutes,
            PendingEntryMinutes = embedded?.PendingEntryMinutes ?? 0,
            PendingExitMinutes = embedded?.PendingExitMinutes ?? 0,
            SiteNumber = embedded?.SiteNumber,
            PolygonJson = string.IsNullOrWhiteSpace(polygonJson) ? PolygonJson(embedded) : polygonJson.Trim()
        };
        db.SiteGeofences.Add(item);
        return item;
    }

    private void ApplySiteLink(SiteGeofence item, string? siteNumber, Guid? siteId, bool? locationOnly)
    {
        if (locationOnly == true)
        {
            item.SiteNumber = "LOCATION_ONLY";
            item.SiteId = null;
            return;
        }

        var cleanSiteNumber = Clean(siteNumber);
        var site = ResolveSite(cleanSiteNumber, siteId);
        item.SiteNumber = site?.ExternalCode ?? cleanSiteNumber;
        item.SiteId = site?.Id ?? siteId;
    }

    private Site? ResolveSite(string? siteNumber, Guid? siteId)
    {
        if (siteId is not null)
        {
            var byId = db.Sites.Local.FirstOrDefault(x => x.Id == siteId.Value)
                ?? db.Sites.FirstOrDefault(x => x.Id == siteId.Value);
            if (byId is not null) return byId;
        }

        var normalized = NormalizeCode(siteNumber);
        if (normalized.Length == 0) return null;
        var numeric = NumericCode(siteNumber);
        return db.Sites
            .Where(x => x.Active)
            .AsEnumerable()
            .FirstOrDefault(x => NormalizeCode(x.ExternalCode) == normalized || (numeric.Length > 0 && NumericCode(x.ExternalCode) == numeric));
    }

    private async Task<object> GeofenceResponse(SiteGeofence item, CancellationToken ct)
    {
        var site = item.SiteId is null ? null : await db.Sites.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.SiteId.Value, ct);
        var locationOnly = string.Equals(item.SiteNumber, "LOCATION_ONLY", StringComparison.OrdinalIgnoreCase);
        return new
        {
            item.Id,
            item.Name,
            item.Category,
            item.CategoryMaxWaitMinutes,
            item.MaxWaitMinutes,
            item.PendingEntryMinutes,
            item.PendingExitMinutes,
            siteNumber = locationOnly ? null : item.SiteNumber,
            item.SiteId,
            siteCode = site?.ExternalCode,
            siteName = site?.Name,
            locationOnly,
            item.Active,
            item.UpdatedAtUtc
        };
    }

    private static string PolygonJson(EmbeddedFence? fence) =>
        fence is null ? "[]" : JsonSerializer.Serialize(fence.Points.Select(point => new[] { point.Longitude, point.Latitude }));
}

public sealed record DriverUpdateRequest(string? DisplayName, string? EmployeeNumber, string? TachoName, string? MobileNumber, string? DriverType, string? DriverGroup, string? Skills);
public sealed record VehicleUpdateRequest(
    string? Registration,
    string? VIN,
    string? OwnerType,
    string? VehicleSite,
    string? FleetNumber,
    string? Abbreviation,
    string? Transmission,
    bool? DvsCompliant,
    string? FuelProvider,
    string? CabMobile,
    string? FuelPin,
    string? ShellCard,
    string? BpRedCard,
    string? BpPlainCard,
    string? FuelPinSecretName,
    string? FuelCardLastFour,
    DateOnly? MOTExpiry,
    DateOnly? TachoCalibrationExpiry,
    DateOnly? VehicleTestExpiry,
    string? Notes,
    string? FleetioId,
    string? FleetioName,
    string? FleetioStatus);
public sealed record TrailerUpdateRequest(string? TrailerNumber, string? Type, int? StandardCapacity, int? EuroCapacity, string? Notes);
public sealed record SiteUpdateRequest(
    string? ExternalCode,
    string? CustomerCode,
    string? Name,
    string? DriverTextName,
    string? CollectionAddress,
    string? CollectionInstructions,
    string? MapLink,
    decimal? Latitude,
    decimal? Longitude,
    string? Aliases,
    string? CustomField1,
    string? CustomField2,
    string? CustomField3,
    string? RoadrunnerCode,
    string? RoadrunnerProfileJson,
    string? OperationalRegion);
public sealed record CustomerUpdateRequest(string? Code, string? Name, string? TradingName, string? AccountOwner, string? ServiceNotes, string? DefaultSiteCode);
public sealed record GeofenceUpdateRequest(string? Name, string? Category, int? CategoryMaxWaitMinutes, int? MaxWaitMinutes, int PendingEntryMinutes, int PendingExitMinutes, string? SiteNumber, Guid? SiteId, bool? LocationOnly, string? PolygonJson);
public sealed record GeofenceSiteSyncRequest(string? Name, string? SiteNumber, Guid? SiteId, bool? LocationOnly, string? PolygonJson);
