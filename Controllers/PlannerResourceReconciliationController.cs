using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning")]
[Authorize]
public sealed class PlannerResourceReconciliationController(TmsDbContext db) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    [HttpPost("reconcile-resources/{date:datetime}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ReconcileResources(DateTime date, CancellationToken ct)
    {
        var planningDate = DateOnly.FromDateTime(date);
        var prefix = $"planimport:{planningDate:yyyyMMdd}:";

        var imports = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == "plannerplanrun" && x.IdempotencyKey.StartsWith(prefix) && x.Status == StagingStatus.Promoted)
            .OrderBy(x => x.IdempotencyKey)
            .ToListAsync(ct);

        var activeDrivers = await db.Drivers.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var activeVehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var activeTrailers = await db.Trailers.AsNoTracking().Where(x => x.Active).ToListAsync(ct);

        List<Load> loads;
        var registerFallback = false;
        try
        {
            // Include stops because every reconciled load is mirrored into the resilient planning
            // register below. That keeps Planner/Runs consistent if either screen later has to use
            // the register fallback because an optional planning table/column is unavailable.
            loads = await db.Loads.Include(x => x.Stops).Where(x => x.PlanningDate == planningDate).ToListAsync(ct);
        }
        catch (Exception ex) when (IsSchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            loads = await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct);
            registerFallback = true;
        }

        var changed = 0;
        var unchanged = 0;
        var unresolvedDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedVehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedTrailers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<object>();

        foreach (var staged in imports)
        {
            PlannerPlanRunRequest? run;
            try { run = JsonSerializer.Deserialize<PlannerPlanRunRequest>(staged.PayloadJson, JsonOptions); }
            catch (JsonException) { continue; }
            if (run is null || string.IsNullOrWhiteSpace(run.RunRef)) continue;

            var reference = PlannerPlanImportRules.TmsReference(planningDate, run.RunRef);
            var load = loads.SingleOrDefault(x => string.Equals(x.Reference, reference, StringComparison.OrdinalIgnoreCase));
            if (load is null) continue;

            var beforeDriver = load.DriverId;
            var beforeVehicle = load.VehicleId;
            var beforeTrailer = load.TrailerId;
            var beforeStatus = load.Status;

            if (load.DriverId is null && !string.IsNullOrWhiteSpace(run.Driver) && !IsPlaceholder(run.Driver))
            {
                var driver = ResolveDriver(activeDrivers, run.Driver);
                if (driver is not null) load.DriverId = driver.Id;
                else unresolvedDrivers.Add(run.Driver.Trim());
            }

            if (load.VehicleId is null && !string.IsNullOrWhiteSpace(run.Vehicle))
            {
                var vehicle = ResolveVehicle(activeVehicles, run.Vehicle);
                if (vehicle is not null) load.VehicleId = vehicle.Id;
                else unresolvedVehicles.Add(run.Vehicle.Trim());
            }

            if (load.TrailerId is null && !string.IsNullOrWhiteSpace(run.Trailer))
            {
                var trailer = ResolveTrailer(activeTrailers, run.Trailer);
                if (trailer is not null) load.TrailerId = trailer.Id;
                else unresolvedTrailers.Add(run.Trailer.Trim());
            }

            if (load.DriverId is not null && load.VehicleId is not null && load.Status == LoadStatus.Draft)
                load.Status = LoadStatus.Planned;

            var didChange = beforeDriver != load.DriverId || beforeVehicle != load.VehicleId || beforeTrailer != load.TrailerId || beforeStatus != load.Status;

            if (!registerFallback && didChange)
                await db.SaveChangesAsync(ct);

            // Always refresh the audited planning-register copy, even when the SQL Load already
            // contained the right resources. Planner and Runs deliberately fall back to this copy
            // during optional schema drift; leaving it stale made imported driver/vehicle evidence
            // visible on the import overview but absent from those operational screens.
            await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);

            if (didChange)
            {
                changed++;
                results.Add(new { run = run.RunRef, reference, outcome = "Backfilled", driverId = load.DriverId, vehicleId = load.VehicleId, trailerId = load.TrailerId });
            }
            else
            {
                unchanged++;
                results.Add(new { run = run.RunRef, reference, outcome = "VerifiedAndMirrored", driverId = load.DriverId, vehicleId = load.VehicleId, trailerId = load.TrailerId });
            }
        }

        return Ok(new
        {
            planningDate,
            sourceRuns = imports.Count,
            changed,
            unchanged,
            unresolvedDrivers = unresolvedDrivers.OrderBy(x => x).ToArray(),
            unresolvedVehicles = unresolvedVehicles.OrderBy(x => x).ToArray(),
            unresolvedTrailers = unresolvedTrailers.OrderBy(x => x).ToArray(),
            results
        });
    }

    internal static Driver? ResolveDriver(IEnumerable<Driver> drivers, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsPlaceholder(value)) return null;
        var needle = Normalize(value);
        var candidates = drivers.Where(x =>
                Normalize(x.DisplayName) == needle ||
                Normalize(x.TachoName) == needle ||
                Normalize(x.EmployeeNumber) == needle)
            .DistinctBy(x => x.Id)
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    internal static Vehicle? ResolveVehicle(IEnumerable<Vehicle> vehicles, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var needle = Normalize(value);
        var exact = vehicles.Where(x => Normalize(x.Registration) == needle || Normalize(x.Abbreviation) == needle || Normalize(x.FleetNumber) == needle)
            .DistinctBy(x => x.Id).Take(2).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        var suffix = vehicles.Where(x => Normalize(x.Registration).EndsWith(needle, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(x => x.Id).Take(2).ToList();
        return suffix.Count == 1 ? suffix[0] : null;
    }

    internal static Trailer? ResolveTrailer(IEnumerable<Trailer> trailers, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var needle = Normalize(value);
        var candidates = trailers.Where(x => Normalize(x.TrailerNumber) == needle)
            .DistinctBy(x => x.Id).Take(2).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool IsPlaceholder(string? value) =>
        string.Equals(value?.Trim(), "c/o", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value?.Trim(), "tbc", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }
}