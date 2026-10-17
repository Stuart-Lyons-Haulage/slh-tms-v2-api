using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/diagnostics")]
[Authorize]
public sealed class DiagnosticsController(TmsDbContext db) : ControllerBase
{
    [HttpGet("data-readiness"), AllowAnonymous]
    public async Task<IActionResult> DataReadiness(CancellationToken ct)
    {
        var checks = new Dictionary<string, Func<CancellationToken, Task<object>>>
        {
            ["customers"] = token => Verify(db.Customers.AsNoTracking(), token),
            ["customerContacts"] = token => Verify(db.CustomerContacts.AsNoTracking(), token),
            ["vehicles"] = token => Verify(db.Vehicles.AsNoTracking(), token),
            ["drivers"] = token => Verify(db.Drivers.AsNoTracking(), token),
            ["trailers"] = token => Verify(db.Trailers.AsNoTracking(), token),
            ["sites"] = token => Verify(db.Sites.AsNoTracking(), token),
            ["marketContacts"] = token => Verify(db.MarketContacts.AsNoTracking(), token),
            ["fuelPrices"] = token => Verify(db.FuelPrices.AsNoTracking(), token),
            ["staging"] = token => Verify(db.StagedImports.AsNoTracking(), token),
            ["orders"] = VerifyOrders,
            ["loads"] = VerifyLoads,
            ["loadStops"] = VerifyLoadStops
        };

        var results = new Dictionary<string, object>();
        var ready = true;
        foreach (var check in checks)
        {
            try { results[check.Key] = await check.Value(ct); }
            catch (Exception ex)
            {
                ready = false;
                results[check.Key] = new { ok = false, error = SanitiseSchemaError(ex.GetBaseException().Message) };
            }
        }

        var response = new { status = ready ? "Healthy" : "Unhealthy", checks = results };
        return ready ? Ok(response) : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }

    [HttpGet("tables")]
    public async Task<IActionResult> Tables(CancellationToken ct)
    {
        var checks = new Dictionary<string, Func<CancellationToken, Task<object>>>
        {
            ["customers"] = token => Verify(db.Customers.AsNoTracking(), token),
            ["customerContacts"] = token => Verify(db.CustomerContacts.AsNoTracking(), token),
            ["vehicles"] = token => Verify(db.Vehicles.AsNoTracking(), token),
            ["drivers"] = token => Verify(db.Drivers.AsNoTracking(), token),
            ["trailers"] = token => Verify(db.Trailers.AsNoTracking(), token),
            ["sites"] = token => Verify(db.Sites.AsNoTracking(), token),
            ["marketContacts"] = token => Verify(db.MarketContacts.AsNoTracking(), token),
            ["staging"] = token => Verify(db.StagedImports.AsNoTracking(), token),
            ["orders"] = token => Verify(db.TransportOrders.AsNoTracking(), token),
            ["loads"] = token => Verify(db.Loads.AsNoTracking(), token),
            ["loadStops"] = token => Verify(db.LoadStops.AsNoTracking(), token),
            ["vehicleLiveStatuses"] = token => Verify(db.VehicleLiveStatuses.AsNoTracking(), token)
        };

        var results = new Dictionary<string, object>();
        foreach (var check in checks)
        {
            try { results[check.Key] = await check.Value(ct); }
            catch (Exception ex) { results[check.Key] = new { ok = false, error = ex.GetBaseException().Message }; }
        }

        return Ok(results);
    }

    [HttpGet("master-data-suggestions")]
    public async Task<IActionResult> MasterDataSuggestions(CancellationToken ct)
    {
        var suggestions = new List<object>();
        var drivers = await db.Drivers.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        suggestions.AddRange(drivers.Where(x => string.IsNullOrWhiteSpace(x.TachoName)).Select(x => new { severity = "warning", entity = "Driver", key = x.EmployeeNumber, message = $"{x.DisplayName} has no TachoMaster name; card-holder matching may fail." }));
        suggestions.AddRange(drivers.Where(x => string.IsNullOrWhiteSpace(x.MobileNumber)).Select(x => new { severity = "warning", entity = "Driver", key = x.EmployeeNumber, message = $"{x.DisplayName} has no mobile number for dispatch texts." }));
        suggestions.AddRange(drivers.Where(x => x.LicenceExpiry is null || x.LicenceExpiry <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30))).Select(x => new { severity = "warning", entity = "Driver", key = x.EmployeeNumber, message = $"{x.DisplayName} needs a licence expiry check or is due within 30 days." }));
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        suggestions.AddRange(sites.Where(x => string.IsNullOrWhiteSpace(x.CollectionAddress)).Select(x => new { severity = "error", entity = "Site", key = x.ExternalCode, message = $"{x.Name} has no collection address; route calculation cannot be trusted." }));
        suggestions.AddRange(sites.Where(x => string.IsNullOrWhiteSpace(x.MapLink)).Select(x => new { severity = "warning", entity = "Site", key = x.ExternalCode, message = $"{x.Name} has no map link; confirm the route point." }));
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        suggestions.AddRange(vehicles.Where(x => string.IsNullOrWhiteSpace(x.Abbreviation)).Select(x => new { severity = "warning", entity = "Vehicle", key = x.Registration, message = $"{x.Registration} has no RoadTech matching abbreviation." }));
        return Ok(new { generatedAtUtc = DateTimeOffset.UtcNow, source = "deterministic-master-data-rules", suggestions = suggestions.Take(200) });
    }

    private static async Task<object> Verify<TEntity>(IQueryable<TEntity> query, CancellationToken ct)
    {
        var count = await query.CountAsync(ct);
        // COUNT(*) can succeed even when mapped columns are missing. Reading one
        // row verifies the same shape used by the portal and Master Import.
        await query.Take(1).ToListAsync(ct);
        return new { ok = true, count };
    }

    private async Task<object> VerifyOrders(CancellationToken ct)
    {
        try { return await Verify(db.TransportOrders.AsNoTracking(), ct); }
        catch
        {
            db.ChangeTracker.Clear();
            var rows = await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct);
            return new { ok = true, count = rows.Count, storage = "audited-register" };
        }
    }

    private async Task<object> VerifyLoads(CancellationToken ct)
    {
        try { return await Verify(db.Loads.AsNoTracking(), ct); }
        catch
        {
            db.ChangeTracker.Clear();
            var rows = await PlanningRegisterStore.ReadLoadsAsync(db, null, ct);
            return new { ok = true, count = rows.Count, storage = "audited-register" };
        }
    }

    private async Task<object> VerifyLoadStops(CancellationToken ct)
    {
        try { return await Verify(db.LoadStops.AsNoTracking(), ct); }
        catch
        {
            db.ChangeTracker.Clear();
            var rows = await PlanningRegisterStore.ReadLoadsAsync(db, null, ct);
            return new { ok = true, count = rows.Sum(load => load.Stops.Count), storage = "audited-register" };
        }
    }

    private static string SanitiseSchemaError(string message)
    {
        if (message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase))
            return message;
        return "The table could not be read using the current application schema.";
    }
}
