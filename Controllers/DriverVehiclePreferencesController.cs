using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/driver-vehicle-preferences"), Authorize]
public sealed class DriverVehiclePreferencesController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    ILogger<DriverVehiclePreferencesController> logger) : ControllerBase
{
    private const string DetailType = "masterdetail:driver";

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, [FromQuery] int days = 28, CancellationToken ct = default)
    {
        var planningDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var window = Math.Clamp(days, 7, 42);
        var persisted = await ReadPersistedAsync(ct);
        var protection = await BuildProtectionAsync(persisted, planningDate, ct);
        return Ok(new { planningDate, lookbackDays = window, generatedAtUtc = DateTimeOffset.UtcNow, preferences = protection });
    }

    [HttpPost("refresh"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Refresh([FromQuery] int days = 28, CancellationToken ct = default)
    {
        var window = Math.Clamp(days, 7, 42);
        var recommendations = await InferAsync(window, ct);
        var applied = 0;
        var skipped = 0;
        var changes = new List<string>();

        foreach (var item in recommendations)
        {
            if (!item.AutoAssign)
            {
                skipped++;
                continue;
            }

            await SavePreferenceAsync(item, ct);
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Driver",
                EntityId = item.DriverId,
                Action = "PreferredVehicleInferred",
                ChangedBy = User.Identity?.Name ?? "driver-vehicle-preference-engine",
                ChangesJson = JsonSerializer.Serialize(new
                {
                    preferredVehicleId = item.VehicleId,
                    preferredVehicleRegistration = item.VehicleRegistration,
                    item.ConfidencePercent,
                    item.ObservedDays,
                    item.TachoDays,
                    item.DotDays,
                    lookbackDays = window,
                    source = "TachoMaster + DOT/Falcon historical average"
                })
            });
            applied++;
            changes.Add($"{item.DriverName} → {item.VehicleRegistration} ({item.ConfidencePercent:0}% across {item.ObservedDays} observed day(s))");
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        return Ok(new
        {
            lookbackDays = window,
            generatedAtUtc = DateTimeOffset.UtcNow,
            applied,
            skipped,
            changes,
            recommendations
        });
    }

    [HttpGet("recommendations")]
    public async Task<IActionResult> Recommendations([FromQuery] int days = 28, CancellationToken ct = default)
    {
        var window = Math.Clamp(days, 7, 42);
        var recommendations = await InferAsync(window, ct);
        return Ok(new { lookbackDays = window, generatedAtUtc = DateTimeOffset.UtcNow, recommendations });
    }

    [HttpGet("allocation-check")]
    public async Task<IActionResult> AllocationCheck(
        [FromQuery] Guid vehicleId,
        [FromQuery] Guid? driverId,
        [FromQuery] DateOnly date,
        CancellationToken ct = default)
    {
        var preferences = await ReadPersistedAsync(ct);
        var preference = preferences.FirstOrDefault(item => item.VehicleId == vehicleId);
        if (preference is null || preference.DriverId == driverId)
            return Ok(new { warning = false, protectedVehicle = false, prompt = (string?)null });

        var protection = (await BuildProtectionAsync([preference], date, ct)).Single();
        return Ok(new
        {
            warning = true,
            protectedVehicle = protection.Protected,
            preference.DriverId,
            preference.DriverName,
            preference.VehicleId,
            preference.VehicleRegistration,
            preference.ConfidencePercent,
            preference.ObservedDays,
            protection.NextPlannedDate,
            prompt = protection.Prompt
        });
    }

    private async Task<List<DriverVehiclePreferenceRecommendation>> InferAsync(int days, CancellationToken ct)
    {
        var end = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = end.AddDays(-(days - 1));
        var drivers = await db.Drivers.AsNoTracking().Where(item => item.Active).OrderBy(item => item.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        drivers = drivers.Where(IsEmployedDriver).ToList();
        var vehicles = await db.Vehicles.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        var vehicleLookup = VehicleLookup(vehicles);

        var votes = drivers.ToDictionary(item => item.Id, _ => new Dictionary<Guid, Evidence>());
        if (tachoMaster.IsConfigured)
        {
            for (var day = start; day <= end; day = day.AddDays(1))
            {
                IReadOnlyList<TachoDriverDutyStatus> duties;
                try { duties = await tachoMaster.GetDriverDutyStatusesAsync(day, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Preferred vehicle inference could not read TachoMaster duties for {Date}.", day);
                    continue;
                }

                foreach (var duty in duties.Where(item => !string.IsNullOrWhiteSpace(item.VehicleCode)))
                {
                    var driver = drivers.FirstOrDefault(item => DriverMatches(item, duty.DriverName, duty.CardNumber, duty.EmployeeNumber, duty.MemberCode));
                    if (driver is null || !TryVehicle(vehicleLookup, duty.VehicleCode, out var vehicle)) continue;
                    AddEvidence(votes[driver.Id], vehicle.Id, day, tacho: true, dot: false);
                }
            }
        }

        var fromUtc = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var dotEvents = await db.VehicleTrackingEvents.AsNoTracking()
            .Where(item => item.EventTimeUtc >= fromUtc)
            .OrderBy(item => item.EventTimeUtc)
            .Take(50000)
            .ToListAsync(ct);
        foreach (var tracking in dotEvents)
        {
            try
            {
                var provider = JsonSerializer.Deserialize<RoadTechTelemetryItem>(tracking.RawPayload, RoadTechJson.Options);
                if (provider is null) continue;
                var record = DotTelemetryRecord.FromProvider(provider);
                if (string.IsNullOrWhiteSpace(record.DriverName) && string.IsNullOrWhiteSpace(record.DriverCardNumber)) continue;
                var driver = drivers.FirstOrDefault(item => DriverMatches(item, record.DriverName, record.DriverCardNumber, null, 0));
                if (driver is null || !TryVehicle(vehicleLookup, tracking.VehicleIdentifier, out var vehicle)) continue;
                AddEvidence(votes[driver.Id], vehicle.Id, DateOnly.FromDateTime(tracking.EventTimeUtc.UtcDateTime), tacho: false, dot: true);
            }
            catch (JsonException) { }
        }

        var result = new List<DriverVehiclePreferenceRecommendation>();
        foreach (var driver in drivers)
        {
            var scored = votes[driver.Id]
                .Select(pair => new
                {
                    VehicleId = pair.Key,
                    Evidence = pair.Value,
                    Score = pair.Value.TachoDays.Count * 2m + pair.Value.DotDays.Count
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ToList();
            if (scored.Count == 0) continue;

            var top = scored[0];
            var total = scored.Sum(item => item.Score);
            var second = scored.Skip(1).FirstOrDefault()?.Score ?? 0m;
            var confidence = total <= 0 ? 0 : top.Score / total * 100m;
            var lead = total <= 0 ? 0 : (top.Score - second) / total * 100m;
            var observedDays = top.Evidence.TachoDays.Union(top.Evidence.DotDays).Count();
            var autoAssign = observedDays >= 4 && confidence >= 60m && lead >= 15m;
            var vehicle = vehicles.First(item => item.Id == top.VehicleId);
            result.Add(new DriverVehiclePreferenceRecommendation(
                driver.Id,
                driver.DisplayName,
                driver.EmployeeNumber,
                vehicle.Id,
                vehicle.Registration,
                Math.Round(confidence, 1),
                observedDays,
                top.Evidence.TachoDays.Count,
                top.Evidence.DotDays.Count,
                Math.Round(lead, 1),
                autoAssign,
                autoAssign
                    ? "Strong regular pairing: safe to assign as the driver's preferred vehicle."
                    : "Pairing retained as a suggestion only because the evidence is not yet strong enough for automatic assignment."));
        }

        return result.OrderByDescending(item => item.AutoAssign).ThenByDescending(item => item.ConfidencePercent).ThenBy(item => item.DriverName).ToList();
    }

    private async Task SavePreferenceAsync(DriverVehiclePreferenceRecommendation item, CancellationToken ct)
    {
        var key = $"{DetailType}:{Normalise(item.EmployeeNumber).ToLowerInvariant()}";
        var row = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        Dictionary<string, object?> payload;
        try
        {
            payload = row is null
                ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(row.PayloadJson) ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { payload = new(StringComparer.OrdinalIgnoreCase); }

        payload["employeeNumber"] = item.EmployeeNumber;
        payload["preferredVehicleId"] = item.VehicleId;
        payload["preferredVehicleRegistration"] = item.VehicleRegistration;
        payload["preferredVehicleConfidencePercent"] = item.ConfidencePercent;
        payload["preferredVehicleObservedDays"] = item.ObservedDays;
        payload["preferredVehicleTachoDays"] = item.TachoDays;
        payload["preferredVehicleDotDays"] = item.DotDays;
        payload["preferredVehicleUpdatedUtc"] = DateTimeOffset.UtcNow;
        payload["preferredVehicleSource"] = "TachoMaster + DOT/Falcon historical average";
        var json = JsonSerializer.Serialize(payload);
        await MasterDetailStore.SaveAsync(db, "driver", item.EmployeeNumber, json, "Driver vehicle preference engine", User.Identity?.Name, ct);
    }

    private async Task<List<PersistedPreference>> ReadPersistedAsync(CancellationToken ct)
    {
        var drivers = await db.Drivers.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == DetailType && item.Status == StagingStatus.Promoted)
            .OrderByDescending(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .Take(5000).ToListAsync(ct);
        var result = new List<PersistedPreference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                var employee = Text(root, "employeeNumber");
                if (string.IsNullOrWhiteSpace(employee) || !seen.Add(Normalise(employee))) continue;
                var driver = drivers.FirstOrDefault(item => Normalise(item.EmployeeNumber) == Normalise(employee));
                if (driver is null || !IsEmployedDriver(driver)) continue;
                var vehicleIdText = Text(root, "preferredVehicleId");
                var registration = Text(root, "preferredVehicleRegistration");
                Vehicle? vehicle = null;
                if (Guid.TryParse(vehicleIdText, out var vehicleId)) vehicle = vehicles.FirstOrDefault(item => item.Id == vehicleId);
                vehicle ??= vehicles.FirstOrDefault(item => Normalise(item.Registration) == Normalise(registration));
                if (vehicle is null) continue;
                result.Add(new PersistedPreference(
                    driver.Id,
                    driver.DisplayName,
                    vehicle.Id,
                    vehicle.Registration,
                    Decimal(root, "preferredVehicleConfidencePercent") ?? 0m,
                    Int(root, "preferredVehicleObservedDays") ?? 0,
                    DateTimeOffset.TryParse(Text(root, "preferredVehicleUpdatedUtc"), out var updated) ? updated : null));
            }
            catch (JsonException) { }
        }
        return result;
    }

    private async Task<List<ProtectedPreference>> BuildProtectionAsync(IReadOnlyCollection<PersistedPreference> preferences, DateOnly date, CancellationToken ct)
    {
        if (preferences.Count == 0) return [];
        var driverIds = preferences.Select(item => item.DriverId).Distinct().ToList();
        var loads = await db.Loads.AsNoTracking()
            .Where(item => item.DriverId != null && driverIds.Contains(item.DriverId.Value) && item.Status != LoadStatus.Cancelled && item.PlanningDate >= date && item.PlanningDate <= date.AddDays(2))
            .OrderBy(item => item.PlanningDate).ThenBy(item => item.CreatedAtUtc)
            .ToListAsync(ct);
        return preferences.Select(item =>
        {
            var worksOnDate = loads.Any(load => load.DriverId == item.DriverId && load.PlanningDate == date);
            var next = loads.FirstOrDefault(load => load.DriverId == item.DriverId && load.PlanningDate > date);
            var protectedVehicle = !worksOnDate && next is not null;
            var prompt = protectedVehicle
                ? $"Preferred vehicle conflict: {item.VehicleRegistration} is normally used by {item.DriverName} ({item.ConfidencePercent:0}% pairing over {item.ObservedDays} observed day(s)). {item.DriverName} is not planned on {date:dd/MM/yyyy} and is next planned on {next!.PlanningDate:dd/MM/yyyy}. Avoid sending this vehicle away if it may not return for that duty."
                : $"Preferred vehicle notice: {item.VehicleRegistration} is normally used by {item.DriverName} ({item.ConfidencePercent:0}% pairing over {item.ObservedDays} observed day(s)). Check that reallocating it will not affect the driver's next duty.";
            return new ProtectedPreference(item.DriverId, item.DriverName, item.VehicleId, item.VehicleRegistration, item.ConfidencePercent, item.ObservedDays, item.UpdatedAtUtc, protectedVehicle, next?.PlanningDate, prompt);
        }).ToList();
    }

    private static bool IsEmployedDriver(Driver driver)
    {
        var type = (driver.DriverType ?? string.Empty).Trim();
        var group = (driver.DriverGroup ?? string.Empty).Trim();
        return !type.Contains("agency", StringComparison.OrdinalIgnoreCase)
            && !group.Contains("agency", StringComparison.OrdinalIgnoreCase)
            && !type.Contains("subcontract", StringComparison.OrdinalIgnoreCase)
            && !group.Contains("subcontract", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, Vehicle> VehicleLookup(IEnumerable<Vehicle> vehicles)
    {
        var result = new Dictionary<string, Vehicle>(StringComparer.OrdinalIgnoreCase);
        foreach (var vehicle in vehicles)
            foreach (var key in new[] { vehicle.Registration, vehicle.Abbreviation, vehicle.FleetNumber }.Where(value => !string.IsNullOrWhiteSpace(value)))
                result.TryAdd(Normalise(key), vehicle);
        return result;
    }

    private static bool TryVehicle(Dictionary<string, Vehicle> lookup, string? value, out Vehicle vehicle)
    {
        if (lookup.TryGetValue(Normalise(value), out vehicle!)) return true;
        var key = Normalise(value);
        var match = lookup.FirstOrDefault(pair => pair.Key.EndsWith(key, StringComparison.OrdinalIgnoreCase) || key.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase));
        vehicle = match.Value!;
        return vehicle is not null;
    }

    private static bool DriverMatches(Driver driver, string? name, string? card, string? employeeNumber, int memberCode)
    {
        if (memberCode > 0 && int.TryParse(driver.TachoMasterDriverId, out var linked) && linked == memberCode) return true;
        if (!string.IsNullOrWhiteSpace(employeeNumber) && Normalise(driver.EmployeeNumber) == Normalise(employeeNumber)) return true;
        if (SameCard(driver.TachoCardNumber, card)) return true;
        var source = Normalise(name);
        return source.Length > 0 && new[] { driver.DisplayName, driver.TachoName }.Where(value => !string.IsNullOrWhiteSpace(value)).Any(value => Normalise(value) == source);
    }

    private static bool SameCard(string? left, string? right)
    {
        var a = Normalise(left); var b = Normalise(right);
        if (a.Length < 8 || b.Length < 8) return false;
        return a == b || a.EndsWith(b, StringComparison.OrdinalIgnoreCase) || b.EndsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddEvidence(Dictionary<Guid, Evidence> values, Guid vehicleId, DateOnly date, bool tacho, bool dot)
    {
        if (!values.TryGetValue(vehicleId, out var evidence)) values[vehicleId] = evidence = new Evidence();
        if (tacho) evidence.TachoDays.Add(date);
        if (dot) evidence.DotDays.Add(date);
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string? Text(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
            if (Normalise(property.Name) == Normalise(name)) return property.Value.ToString();
        return null;
    }
    private static int? Int(JsonElement root, string name) => int.TryParse(Text(root, name), out var value) ? value : null;
    private static decimal? Decimal(JsonElement root, string name) => decimal.TryParse(Text(root, name), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    private sealed class Evidence
    {
        public HashSet<DateOnly> TachoDays { get; } = [];
        public HashSet<DateOnly> DotDays { get; } = [];
    }
}

public sealed record DriverVehiclePreferenceRecommendation(Guid DriverId, string DriverName, string EmployeeNumber, Guid VehicleId, string VehicleRegistration, decimal ConfidencePercent, int ObservedDays, int TachoDays, int DotDays, decimal LeadPercent, bool AutoAssign, string Reason);
public sealed record PersistedPreference(Guid DriverId, string DriverName, Guid VehicleId, string VehicleRegistration, decimal ConfidencePercent, int ObservedDays, DateTimeOffset? UpdatedAtUtc);
public sealed record ProtectedPreference(Guid DriverId, string DriverName, Guid VehicleId, string VehicleRegistration, decimal ConfidencePercent, int ObservedDays, DateTimeOffset? UpdatedAtUtc, bool Protected, DateOnly? NextPlannedDate, string Prompt);