using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record TachoCanonicalOrchestrationResult(
    bool Success,
    TachoDriverMasterSyncResult Canonical,
    IntegrationSyncResult IdentityEnrichment,
    DateTimeOffset CompletedAtUtc,
    string Message);

/// <summary>
/// Single authority for scheduled/manual TachoMaster Driver Master enrichment.
/// Driver Master is the operational authority. Sage HR maintains employed-driver identity/status;
/// TachoMaster enriches existing Driver Master rows with Member Code, card and hours evidence.
/// A Tacho refresh must never deactivate a valid Driver Master row merely because it is not present
/// in the provider's current/eligible worker population.
/// </summary>
public sealed class TachoCanonicalDriverMasterOrchestrator(
    TmsDbContext db,
    IntegrationSyncCoordinator integration,
    DistributedLeaseManager leases,
    ILogger<TachoCanonicalDriverMasterOrchestrator> logger)
{
    public async Task<TachoCanonicalOrchestrationResult> RunAsync(string actor, CancellationToken ct)
    {
        await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.TachoMaster, TimeSpan.FromMinutes(2), ct);
        if (lease is null)
        {
            var now = DateTimeOffset.UtcNow;
            var skipped = new TachoDriverMasterSyncResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                "TachoMaster enrichment skipped because another distributed writer currently holds the integration lease.", now);
            var enrichment = new IntegrationSyncResult("TachoMaster", false, now, skipped.Message);
            return new TachoCanonicalOrchestrationResult(false, skipped, enrichment, now, skipped.Message);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LostToken);
        return await RunCoreAsync(actor, linked.Token);
    }

    private async Task<TachoCanonicalOrchestrationResult> RunCoreAsync(string actor, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        IntegrationSyncResult enrichment;
        TachoDriverMasterSyncResult canonicalResult;

        try
        {
            // This is deliberately non-destructive. IntegrationSyncCoordinator matches existing
            // Driver Master rows by Member Code/card/employee number/name and enriches them.
            // New Sage HR employees therefore remain active while waiting for a confident Tacho match.
            enrichment = await integration.SyncTachoMasterCoreAsync($"{actor}:identity-enrichment", ct);

            var activeDrivers = await db.Drivers.AsNoTracking().Where(driver => driver.Active).ToListAsync(ct);
            await MasterDetailStore.EnrichDriversAsync(db, activeDrivers, ct);
            var withMember = activeDrivers.Count(driver => !string.IsNullOrWhiteSpace(driver.TachoMasterDriverId));
            var withCard = activeDrivers.Count(driver => !string.IsNullOrWhiteSpace(driver.TachoCardNumber));
            var duplicateMembers = activeDrivers
                .Where(driver => !string.IsNullOrWhiteSpace(driver.TachoMasterDriverId))
                .GroupBy(driver => TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoMasterDriverId), StringComparer.OrdinalIgnoreCase)
                .Count(group => group.Key.Length > 0 && group.Count() > 1);
            var duplicateCards = activeDrivers
                .Where(driver => !string.IsNullOrWhiteSpace(driver.TachoCardNumber))
                .GroupBy(driver => TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoCardNumber), StringComparer.OrdinalIgnoreCase)
                .Count(group => group.Key.Length > 0 && group.Count() > 1);

            var completedAt = DateTimeOffset.UtcNow;
            var message = enrichment.Success
                ? $"Non-destructive TachoMaster enrichment completed for Driver Master. {withMember}/{activeDrivers.Count} active drivers have a Member/DB number and {withCard}/{activeDrivers.Count} have card evidence."
                : $"TachoMaster enrichment failed safely without deactivating Driver Master rows. {enrichment.Message}";

            canonicalResult = new TachoDriverMasterSyncResult(
                enrichment.Success,
                withMember,
                activeDrivers.Count,
                0,
                enrichment.Changed,
                0,
                0,
                0,
                0,
                0,
                duplicateMembers,
                activeDrivers.Count - withCard,
                message,
                completedAt);

            logger.LogInformation(
                "Tacho Driver Master enrichment: active={Active}, member-linked={WithMember}, card-linked={WithCard}, duplicate-members={DuplicateMembers}, duplicate-cards={DuplicateCards}. Driver Master rows were not archived.",
                activeDrivers.Count, withMember, withCard, duplicateMembers, duplicateCards);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Non-destructive TachoMaster Driver Master enrichment failed.");
            var completedAt = DateTimeOffset.UtcNow;
            enrichment = new IntegrationSyncResult("TachoMaster", false, completedAt, ex.GetBaseException().Message);
            canonicalResult = new TachoDriverMasterSyncResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                $"TachoMaster enrichment failed safely. {ex.GetBaseException().Message}", completedAt);
        }

        var completed = DateTimeOffset.UtcNow;
        var success = canonicalResult.Success;
        var messageFinal = canonicalResult.Message;

        db.StagedImports.Add(new StagedImport
        {
            EntityType = "tachodrivermasterorchestration",
            IdempotencyKey = $"tachodrivermasterorchestration:{completed:yyyyMMddHHmmss}:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(new
            {
                startedAtUtc = started,
                completedAtUtc = completed,
                success,
                operationalAuthority = "Driver Master",
                employmentAuthority = "Sage HR",
                tachoRole = "Non-destructive identity/card/hours enrichment",
                identityOrder = new[] { "TachoMaster Member/DB number", "Tacho Card Number", "Employee Number", "Unique compatible name" },
                identityEnrichment = new
                {
                    enrichment.Success,
                    enrichment.CompletedAtUtc,
                    enrichment.Message,
                    enrichment.Changed
                },
                driverMaster = new
                {
                    canonicalResult.CanonicalActiveDrivers,
                    memberLinkedDrivers = canonicalResult.SourceWorkers,
                    updated = canonicalResult.Updated,
                    rowsArchivedByTacho = 0,
                    canonicalResult.Message
                }
            }),
            Source = actor.StartsWith("system:", StringComparison.OrdinalIgnoreCase)
                ? "Scheduled non-destructive TachoMaster Driver Master enrichment"
                : "Manual non-destructive TachoMaster Driver Master enrichment",
            Status = success ? StagingStatus.Promoted : StagingStatus.Rejected,
            ReceivedAtUtc = started,
            ReviewedAtUtc = completed,
            ReviewedBy = actor,
            ReviewNote = messageFinal
        });
        await db.SaveChangesAsync(ct);

        return new TachoCanonicalOrchestrationResult(success, canonicalResult, enrichment, completed, messageFinal);
    }
}
