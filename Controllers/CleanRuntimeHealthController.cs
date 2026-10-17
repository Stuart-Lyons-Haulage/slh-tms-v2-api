using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/health/clean-runtime")]
[Authorize(Policy = "TmsAdmin")]
public sealed class CleanRuntimeHealthController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var drivers = await db.Drivers.AsNoTracking()
            .Select(x => new { x.Id, x.Active, x.TachoMasterDriverId, x.EmployeeNumber })
            .ToListAsync(ct);
        var vehicles = await db.Vehicles.AsNoTracking()
            .Select(x => new { x.Id, x.Active, x.Registration, x.FleetioId })
            .ToListAsync(ct);

        var duplicateMemberGroups = drivers
            .Where(x => !string.IsNullOrWhiteSpace(x.TachoMasterDriverId))
            .GroupBy(x => TachoDriverIdentityRules.NormaliseIdentifier(x.TachoMasterDriverId), StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Key.Length > 0 && x.Count() > 1)
            .Select(x => new { identity = x.Key, count = x.Count() })
            .ToArray();

        var duplicateRegistrations = vehicles
            .GroupBy(x => IntegrationSyncCoordinator.CanonicalVehicleRegistration(x.Registration), StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Key.Length > 0 && x.Count() > 1)
            .Select(x => new { registration = x.Key, count = x.Count() })
            .ToArray();

        var duplicateFleetIds = vehicles
            .Where(x => !string.IsNullOrWhiteSpace(x.FleetioId))
            .GroupBy(x => x.FleetioId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .Select(x => new { fleetioId = x.Key, count = x.Count() })
            .ToArray();

        var pendingReview = await db.StagedImports.AsNoTracking()
            .CountAsync(x => x.Status == StagingStatus.PendingReview, ct);
        var pendingDriverReview = await db.StagedImports.AsNoTracking()
            .CountAsync(x => x.EntityType == "driverreview" && x.Status == StagingStatus.PendingReview, ct);
        var retainedProcessedOutboxPayloads = await db.AuditOutboxes.AsNoTracking()
            .CountAsync(x => x.ProcessedAt != null && x.Payload != string.Empty, ct);
        var pendingOutbox = await db.AuditOutboxes.AsNoTracking()
            .CountAsync(x => x.ProcessedAt == null && x.FailedAt == null, ct);

        var duplicateFree = duplicateMemberGroups.Length == 0
            && duplicateRegistrations.Length == 0
            && duplicateFleetIds.Length == 0;

        return Ok(new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            database = db.Database.GetDbConnection().Database,
            readyForCleanV2 = duplicateFree && retainedProcessedOutboxPayloads == 0,
            drivers = new
            {
                total = drivers.Count,
                active = drivers.Count(x => x.Active),
                withTachoMemberCode = drivers.Count(x => !string.IsNullOrWhiteSpace(x.TachoMasterDriverId)),
                activeWithoutTachoMemberCode = drivers.Count(x => x.Active && string.IsNullOrWhiteSpace(x.TachoMasterDriverId)),
                duplicateMemberGroups
            },
            vehicles = new
            {
                total = vehicles.Count,
                active = vehicles.Count(x => x.Active),
                withFleetId = vehicles.Count(x => !string.IsNullOrWhiteSpace(x.FleetioId)),
                duplicateRegistrations,
                duplicateFleetIds
            },
            staging = new { pendingReview, pendingDriverReview },
            auditOutbox = new { pending = pendingOutbox, processedPayloadsStillRetained = retainedProcessedOutboxPayloads }
        });
    }
}
