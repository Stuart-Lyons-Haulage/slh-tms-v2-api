using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Narrow operational recovery endpoints which avoid materialising legacy schema
/// columns and expose concrete TachoMaster data-quality counts.
/// </summary>
[ApiController]
[Route("api/v1/operational-recovery")]
[Authorize(Policy = "TmsWrite")]
public sealed class OperationalRecoveryController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    ILogger<OperationalRecoveryController> logger) : ControllerBase
{
    [HttpDelete("orders/{id:guid}")]
    public async Task<IActionResult> CancelOrder(Guid id, CancellationToken ct)
    {
        // Normal production order first. Only project stable columns so optional
        // legacy fields can never make a cancellation request fail.
        var order = await db.TransportOrders.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new { item.Id, item.Reference, item.Status })
            .SingleOrDefaultAsync(ct);

        if (order is not null)
        {
            if (order.Status == OrderStatus.Delivered)
                return BadRequest(new { message = "A delivered order cannot be deleted." });

            var removedStops = 0;
            string? stopCleanupWarning = null;
            try
            {
                removedStops = await db.LoadStops
                    .Where(stop => stop.OrderId == id)
                    .ExecuteDeleteAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopCleanupWarning = "The job was cancelled, but one or more linked planning stops could not be removed automatically.";
                logger.LogWarning(ex, "Best-effort load-stop cleanup failed while cancelling order {OrderId}.", id);
                db.ChangeTracker.Clear();
            }

            var updated = await db.TransportOrders
                .Where(item => item.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, OrderStatus.Cancelled), ct);

            if (updated == 0)
                return NotFound(new { message = "Order was not found when cancellation was applied." });

            return Ok(new
            {
                order.Id,
                order.Reference,
                status = OrderStatus.Cancelled.ToString(),
                removedStops,
                warning = stopCleanupWarning,
                source = "TransportOrders"
            });
        }

        // When the production planning schema is unavailable the portal intentionally
        // returns orders from the audited StagedImports register. Their visible order
        // ID is the StagedImport ID, so cancellation must understand that store too.
        var registerOrder = await db.StagedImports
            .SingleOrDefaultAsync(item => item.Id == id &&
                (item.EntityType == "order" || item.EntityType == "register:order"), ct);

        if (registerOrder is null)
            return NotFound(new { message = "Order was not found in either the primary order table or the fallback order register." });

        var registerReference = RegisterText(registerOrder.PayloadJson, "poNumber")
            ?? RegisterText(registerOrder.PayloadJson, "reference")
            ?? registerOrder.Id.ToString("N");

        registerOrder.EntityType = "archived:order";
        registerOrder.IdempotencyKey = $"cancelled:{registerOrder.Id:N}:{Guid.NewGuid():N}";
        registerOrder.Status = StagingStatus.Rejected;
        registerOrder.ReviewedAtUtc = DateTimeOffset.UtcNow;
        registerOrder.ReviewedBy = User.Identity?.Name;
        registerOrder.ReviewNote = "Cancelled from Manage Jobs. Audit payload retained and original import key released for re-import.";
        await db.SaveChangesAsync(ct);

        // Fallback planning loads may contain this staged order ID. Remove the stop
        // from their JSON payloads so it cannot remain visually allocated to a run.
        var removedRegisterStops = 0;
        try
        {
            var loadRows = await db.StagedImports
                .Where(item => item.EntityType == "planningload" && item.Status == StagingStatus.Promoted)
                .ToListAsync(ct);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
            foreach (var loadRow in loadRows)
            {
                Load? load;
                try { load = JsonSerializer.Deserialize<Load>(loadRow.PayloadJson, options); }
                catch (JsonException) { continue; }
                if (load is null) continue;
                var before = load.Stops.Count;
                load.Stops = load.Stops.Where(stop => stop.OrderId != id).ToList();
                var removed = before - load.Stops.Count;
                if (removed <= 0) continue;
                removedRegisterStops += removed;
                for (var sequence = 0; sequence < load.Stops.Count; sequence++)
                    load.Stops[sequence].Sequence = sequence + 1;
                loadRow.PayloadJson = JsonSerializer.Serialize(load, options);
                loadRow.ReviewedAtUtc = DateTimeOffset.UtcNow;
                loadRow.ReviewedBy = User.Identity?.Name;
                loadRow.ReviewNote = $"Removed {removed} cancelled order stop(s) from fallback planning load.";
            }
            if (removedRegisterStops > 0) await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fallback load cleanup failed while cancelling register order {OrderId}.", id);
        }

        return Ok(new
        {
            id,
            reference = registerReference,
            status = OrderStatus.Cancelled.ToString(),
            removedStops = removedRegisterStops,
            source = "PlanningRegister"
        });
    }

    [HttpPost("tachomaster/refresh-drivers")]
    public async Task<IActionResult> RefreshTachoDrivers(CancellationToken ct)
    {
        if (!tachoMaster.IsConfigured)
            return BadRequest(new
            {
                configured = false,
                connected = false,
                sourceDrivers = 0,
                profilesWithHours = 0,
                matched = 0,
                currentVehicleDuties = 0,
                missingSettings = tachoMaster.MissingSettings,
                message = "TachoMaster is not configured."
            });

        try
        {
            var profiles = await tachoMaster.GetDriverProfilesAsync(ct);
            var profilesWithHours = profiles.Count(profile =>
                profile.DriveAvailableTodayMinutes is not null ||
                profile.DriveAvailableWeekMinutes is not null ||
                profile.WorkAvailableWeekMinutes is not null);

            var drivers = await db.Drivers
                .Where(driver => driver.Active)
                .OrderBy(driver => driver.DisplayName)
                .ToListAsync(ct);
            await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);

            var byMember = profiles
                .GroupBy(profile => profile.MemberCode.ToString())
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var byCard = profiles
                .Where(profile => !string.IsNullOrWhiteSpace(profile.CardNumber))
                .GroupBy(profile => NormaliseIdentifier(profile.CardNumber!))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var byEmployee = profiles
                .Where(profile => !string.IsNullOrWhiteSpace(profile.EmployeeNumber))
                .GroupBy(profile => NormaliseIdentifier(profile.EmployeeNumber!))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var byName = profiles
                .Where(profile => !string.IsNullOrWhiteSpace(profile.DriverName))
                .GroupBy(profile => NormalisePersonName(profile.DriverName))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var matched = 0;
            var matchedWithHours = 0;
            var matchedByMember = 0;
            var matchedByCard = 0;
            var matchedByEmployee = 0;
            var matchedByTachoName = 0;
            var matchedByDisplayName = 0;
            var now = DateTimeOffset.UtcNow;

            foreach (var driver in drivers)
            {
                TachoDriverProfile? profile = null;
                string? reason = null;

                if (!string.IsNullOrWhiteSpace(driver.TachoMasterDriverId) &&
                    byMember.TryGetValue(driver.TachoMasterDriverId.Trim(), out profile))
                    reason = "MemberId";

                if (profile is null && !string.IsNullOrWhiteSpace(driver.TachoCardNumber) &&
                    byCard.TryGetValue(NormaliseIdentifier(driver.TachoCardNumber), out profile))
                    reason = "CardNumber";

                if (profile is null && !string.IsNullOrWhiteSpace(driver.EmployeeNumber) &&
                    byEmployee.TryGetValue(NormaliseIdentifier(driver.EmployeeNumber), out profile))
                    reason = "EmployeeNumber";

                if (profile is null && !string.IsNullOrWhiteSpace(driver.TachoName) &&
                    byName.TryGetValue(NormalisePersonName(driver.TachoName), out profile))
                    reason = "TachoName";

                if (profile is null && !string.IsNullOrWhiteSpace(driver.DisplayName) &&
                    byName.TryGetValue(NormalisePersonName(driver.DisplayName), out profile))
                    reason = "DisplayName";

                if (profile is null) continue;

                driver.TachoMasterDriverId = profile.MemberCode.ToString();
                driver.TachoCardNumber = profile.CardNumber;
                driver.TachoDriveAvailableTodayMinutes = profile.DriveAvailableTodayMinutes;
                driver.TachoDriveAvailableWeekMinutes = profile.DriveAvailableWeekMinutes;
                driver.TachoWorkAvailableWeekMinutes = profile.WorkAvailableWeekMinutes;
                driver.LastTachoSyncUtc = now;

                await MasterDetailStore.SaveAsync(
                    db,
                    "driver",
                    driver.EmployeeNumber,
                    JsonSerializer.Serialize(driver),
                    $"TachoMaster operational refresh ({reason})",
                    User.Identity?.Name,
                    ct);

                matched++;
                if (profile.DriveAvailableTodayMinutes is not null ||
                    profile.DriveAvailableWeekMinutes is not null ||
                    profile.WorkAvailableWeekMinutes is not null)
                    matchedWithHours++;

                switch (reason)
                {
                    case "MemberId": matchedByMember++; break;
                    case "CardNumber": matchedByCard++; break;
                    case "EmployeeNumber": matchedByEmployee++; break;
                    case "TachoName": matchedByTachoName++; break;
                    case "DisplayName": matchedByDisplayName++; break;
                }
            }

            IReadOnlyDictionary<string, TachoVehicleDriverStatus> duties =
                new Dictionary<string, TachoVehicleDriverStatus>();
            try
            {
                duties = await tachoMaster.GetCurrentDriverStatusesByVehicleAsync(
                    DateOnly.FromDateTime(DateTime.UtcNow), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "TachoMaster current vehicle duty diagnostic failed after driver refresh.");
            }

            var message = profilesWithHours == 0
                ? $"TachoMaster returned {profiles.Count} driver profiles but zero profiles contained drive/work availability metrics. Driver matching cannot create hours that the upstream metric response did not supply."
                : $"TachoMaster returned {profiles.Count} profiles; {profilesWithHours} contained hours. Matched {matched} TMS drivers, including {matchedWithHours} with hours. Current vehicle duties: {duties.Count}.";

            return Ok(new
            {
                configured = true,
                connected = true,
                sourceDrivers = profiles.Count,
                profilesWithHours,
                matched,
                matchedWithHours,
                unmatched = Math.Max(drivers.Count - matched, 0),
                currentVehicleDuties = duties.Count,
                matchReasons = new
                {
                    memberId = matchedByMember,
                    cardNumber = matchedByCard,
                    employeeNumber = matchedByEmployee,
                    tachoName = matchedByTachoName,
                    displayName = matchedByDisplayName
                },
                syncedAtUtc = now,
                message
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "TachoMaster operational refresh failed.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                configured = true,
                connected = false,
                sourceDrivers = 0,
                profilesWithHours = 0,
                matched = 0,
                currentVehicleDuties = 0,
                message = $"TachoMaster operational refresh failed: {ex.GetBaseException().Message}"
            });
        }
    }

    private static string? RegisterText(string payloadJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = new string(property.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                var target = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                if (key == target) return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : property.Value.ToString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string NormaliseIdentifier(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string NormalisePersonName(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
            .Where(word => word.Length > 0)
            .OrderBy(word => word, StringComparer.Ordinal));
}
