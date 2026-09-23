using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record TachoDriverMasterSyncJobStatus(
    Guid JobId,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Message,
    TachoCanonicalOrchestrationResult? Result);

internal sealed record TachoDriverMasterSyncJobClaim(Guid JobId, string Actor);
internal sealed record TachoDriverMasterSyncJobEnvelope(
    string Actor,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? Message = null,
    TachoCanonicalOrchestrationResult? Result = null,
    string? WorkerInstanceId = null,
    DateTimeOffset? HeartbeatUtc = null);

public sealed class TachoDriverMasterSyncJobService(TmsDbContext db)
{
    internal const string EntityType = "tachodrivermastersyncjob";
    internal const string QueueSlotKey = "tachodrivermastersyncjob:singleton";
    internal static readonly TimeSpan LeaseStaleAfter = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan LegacyRunningGrace = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TachoDriverMasterSyncJobStatus> EnqueueAsync(string actor, CancellationToken ct)
    {
        // There is deliberately one reusable queue slot. Without this, two API replicas can both
        // observe an unhealthy Driver Master and insert different randomly keyed jobs, allowing two
        // canonical cleanses to run concurrently. The unique IdempotencyKey plus RowVersion makes
        // creation/requeue contention converge on one durable job row.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await db.StagedImports.AsNoTracking()
                .Where(row => row.EntityType == EntityType &&
                              (row.Status == StagingStatus.PendingReview || row.Status == StagingStatus.Approved))
                .OrderBy(row => row.ReceivedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (existing is not null) return ToStatus(existing);

            var now = DateTimeOffset.UtcNow;
            var envelope = new TachoDriverMasterSyncJobEnvelope(actor, now, Message: "Queued. The canonical cleanse runs independently of the browser request.");
            var source = actor.StartsWith("system:", StringComparison.OrdinalIgnoreCase)
                ? "System canonical Driver Master queue"
                : "Manual canonical Driver Master queue";

            var slot = await db.StagedImports
                .SingleOrDefaultAsync(row => row.EntityType == EntityType && row.IdempotencyKey == QueueSlotKey, ct);
            if (slot is null)
            {
                slot = new StagedImport
                {
                    EntityType = EntityType,
                    IdempotencyKey = QueueSlotKey,
                    PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions),
                    Source = source,
                    Status = StagingStatus.PendingReview,
                    ReceivedAtUtc = now,
                    ReviewedBy = actor,
                    ReviewNote = envelope.Message
                };
                db.StagedImports.Add(slot);
            }
            else
            {
                slot.PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions);
                slot.Source = source;
                slot.Status = StagingStatus.PendingReview;
                slot.ReceivedAtUtc = now;
                slot.ReviewedAtUtc = null;
                slot.ReviewedBy = actor;
                slot.ReviewNote = envelope.Message;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return ToStatus(slot, envelope);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Most commonly another replica created QueueSlotKey between our read and insert.
                db.ChangeTracker.Clear();
            }
        }

        db.ChangeTracker.Clear();
        var winner = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == EntityType &&
                          (row.Status == StagingStatus.PendingReview || row.Status == StagingStatus.Approved))
            .OrderBy(row => row.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (winner is not null) return ToStatus(winner);
        throw new InvalidOperationException("Could not acquire the canonical Driver Master sync queue slot after concurrent retries.");
    }

    public async Task<TachoDriverMasterSyncJobStatus?> GetAsync(Guid jobId, CancellationToken ct)
    {
        var row = await db.StagedImports.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == jobId && item.EntityType == EntityType, ct);
        return row is null ? null : ToStatus(row);
    }

    internal async Task<TachoDriverMasterSyncJobClaim?> TryClaimNextAsync(string workerInstanceId, CancellationToken ct)
    {
        var row = await db.StagedImports
            .Where(item => item.EntityType == EntityType && item.Status == StagingStatus.PendingReview)
            .OrderBy(item => item.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var envelope = ReadEnvelope(row);
        var started = DateTimeOffset.UtcNow;
        envelope = envelope with
        {
            StartedAtUtc = started,
            Message = "Canonical TachoMaster Driver Master sync is running.",
            WorkerInstanceId = workerInstanceId,
            HeartbeatUtc = started
        };
        row.Status = StagingStatus.Approved;
        row.ReviewedAtUtc = started;
        row.ReviewedBy = envelope.Actor;
        row.ReviewNote = envelope.Message;
        row.PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions);
        try
        {
            await db.SaveChangesAsync(ct);
            var claim = new TachoDriverMasterSyncJobClaim(row.Id, envelope.Actor);
            // Heartbeats use separate DbContexts and advance RowVersion. Do not retain a stale
            // tracked copy in the orchestration scope or completion will conflict with our own lease.
            db.ChangeTracker.Clear();
            return claim;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return null;
        }
    }

    internal async Task<bool> HeartbeatAsync(Guid jobId, string workerInstanceId, CancellationToken ct)
    {
        var row = await db.StagedImports
            .SingleOrDefaultAsync(item => item.Id == jobId && item.EntityType == EntityType, ct);
        if (row is null || row.Status != StagingStatus.Approved) return false;

        var envelope = ReadEnvelope(row);
        if (!string.Equals(envelope.WorkerInstanceId, workerInstanceId, StringComparison.Ordinal)) return false;

        row.PayloadJson = JsonSerializer.Serialize(envelope with { HeartbeatUtc = DateTimeOffset.UtcNow }, JsonOptions);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    internal async Task<bool> CompleteAsync(
        Guid jobId,
        string workerInstanceId,
        TachoCanonicalOrchestrationResult result,
        CancellationToken ct)
    {
        var row = await db.StagedImports
            .SingleOrDefaultAsync(item => item.Id == jobId && item.EntityType == EntityType, ct);
        if (row is null || row.Status != StagingStatus.Approved) return false;

        var current = ReadEnvelope(row);
        if (!string.Equals(current.WorkerInstanceId, workerInstanceId, StringComparison.Ordinal)) return false;

        var envelope = current with
        {
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Message = result.Message,
            Result = result,
            HeartbeatUtc = DateTimeOffset.UtcNow
        };
        row.Status = result.Success ? StagingStatus.Promoted : StagingStatus.Failed;
        row.ReviewedAtUtc = envelope.CompletedAtUtc;
        row.ReviewNote = result.Message;
        row.PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    internal async Task<bool> FailAsync(Guid jobId, string workerInstanceId, Exception exception, CancellationToken ct)
    {
        var row = await db.StagedImports
            .SingleOrDefaultAsync(item => item.Id == jobId && item.EntityType == EntityType, ct);
        if (row is null || row.Status != StagingStatus.Approved) return false;

        var current = ReadEnvelope(row);
        if (!string.Equals(current.WorkerInstanceId, workerInstanceId, StringComparison.Ordinal)) return false;

        var message = $"Canonical TachoMaster Driver Master sync failed: {exception.GetBaseException().Message}";
        var envelope = current with
        {
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Message = message,
            HeartbeatUtc = DateTimeOffset.UtcNow
        };
        row.Status = StagingStatus.Failed;
        row.ReviewedAtUtc = envelope.CompletedAtUtc;
        row.ReviewNote = message;
        row.PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    internal async Task<int> RecoverInterruptedAsync(string currentInstanceId, CancellationToken ct)
    {
        _ = currentInstanceId; // Lease freshness, not instance-name equality, is authoritative.
        var now = DateTimeOffset.UtcNow;
        var leaseCutoff = now - LeaseStaleAfter;
        var legacyCutoff = now - LegacyRunningGrace;
        var rows = await db.StagedImports
            .Where(item => item.EntityType == EntityType && item.Status == StagingStatus.Approved)
            .OrderBy(item => item.ReceivedAtUtc)
            .ToListAsync(ct);
        var recovered = 0;

        foreach (var row in rows)
        {
            var envelope = ReadEnvelope(row);
            var hasLease = !string.IsNullOrWhiteSpace(envelope.WorkerInstanceId) && envelope.HeartbeatUtc is not null;
            var staleLease = hasLease && envelope.HeartbeatUtc < leaseCutoff;
            var legacyRunning = !hasLease && (row.ReviewedAtUtc ?? row.ReceivedAtUtc) < legacyCutoff;
            if (!staleLease && !legacyRunning) continue;

            var reason = staleLease
                ? $"Worker lease {envelope.WorkerInstanceId} expired after its last heartbeat."
                : "Legacy running job has no worker lease and outlived the deployment recovery grace period.";
            var message = $"A previous canonical sync worker stopped before completion. {reason} A replacement sync will be queued automatically.";
            var failed = envelope with { CompletedAtUtc = now, Message = message };
            row.Status = StagingStatus.Failed;
            row.ReviewedAtUtc = now;
            row.ReviewNote = message;
            row.PayloadJson = JsonSerializer.Serialize(failed, JsonOptions);

            try
            {
                await db.SaveChangesAsync(ct);
                recovered++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another replica recovered or refreshed this row first. Reload on the next pass.
                db.ChangeTracker.Clear();
                break;
            }
        }

        return recovered;
    }

    private static TachoDriverMasterSyncJobStatus ToStatus(StagedImport row, TachoDriverMasterSyncJobEnvelope? envelope = null)
    {
        envelope ??= ReadEnvelope(row);
        return new TachoDriverMasterSyncJobStatus(
            row.Id,
            StatusName(row.Status),
            envelope.RequestedAtUtc,
            envelope.StartedAtUtc,
            envelope.CompletedAtUtc,
            envelope.Message ?? row.ReviewNote,
            envelope.Result);
    }

    private static TachoDriverMasterSyncJobEnvelope ReadEnvelope(StagedImport row)
    {
        try
        {
            return JsonSerializer.Deserialize<TachoDriverMasterSyncJobEnvelope>(row.PayloadJson, JsonOptions)
                   ?? new TachoDriverMasterSyncJobEnvelope(row.ReviewedBy ?? "unknown", row.ReceivedAtUtc);
        }
        catch (JsonException)
        {
            return new TachoDriverMasterSyncJobEnvelope(row.ReviewedBy ?? "unknown", row.ReceivedAtUtc, Message: row.ReviewNote);
        }
    }

    private static string StatusName(StagingStatus status) => status switch
    {
        StagingStatus.PendingReview => "queued",
        StagingStatus.Approved => "running",
        StagingStatus.Promoted => "succeeded",
        StagingStatus.Failed or StagingStatus.Rejected => "failed",
        _ => status.ToString().ToLowerInvariant()
    };
}

public sealed class TachoDriverMasterSyncJobWorker(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<TachoDriverMasterSyncJobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RecoveryPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("Testing")) return;

        var workerInstanceId = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        var nextRecoveryCheck = DateTimeOffset.MinValue;

        try
        {
            await EnsureCanonicalWorkAsync(workerInstanceId, includeStaleSync: true, stoppingToken);
            nextRecoveryCheck = DateTimeOffset.UtcNow + RecoveryPollInterval;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not initialise the Driver Master sync queue; the worker will continue polling.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = false;
            try
            {
                if (DateTimeOffset.UtcNow >= nextRecoveryCheck)
                {
                    await EnsureCanonicalWorkAsync(workerInstanceId, includeStaleSync: false, stoppingToken);
                    nextRecoveryCheck = DateTimeOffset.UtcNow + RecoveryPollInterval;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<TachoDriverMasterSyncJobService>();
                var claim = await jobs.TryClaimNextAsync(workerInstanceId, stoppingToken);
                if (claim is not null)
                {
                    processed = true;
                    using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var heartbeatTask = MaintainLeaseAsync(claim.JobId, workerInstanceId, jobCts, stoppingToken);
                    try
                    {
                        var orchestrator = scope.ServiceProvider.GetRequiredService<TachoCanonicalDriverMasterOrchestrator>();
                        var result = await orchestrator.RunAsync(claim.Actor, jobCts.Token);
                        if (!await jobs.CompleteAsync(claim.JobId, workerInstanceId, result, stoppingToken))
                            logger.LogWarning("Canonical Driver Master sync {JobId} finished after its worker lease was lost; completion was not written over the replacement worker.", claim.JobId);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (OperationCanceledException)
                    {
                        logger.LogWarning("Canonical Driver Master sync {JobId} stopped because its worker lease was lost.", claim.JobId);
                        await jobs.FailAsync(
                            claim.JobId,
                            workerInstanceId,
                            new InvalidOperationException("Canonical Driver Master worker lease was lost before completion."),
                            stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Queued canonical Driver Master sync {JobId} failed unexpectedly.", claim.JobId);
                        await jobs.FailAsync(claim.JobId, workerInstanceId, ex, stoppingToken);
                    }
                    finally
                    {
                        jobCts.Cancel();
                        try { await heartbeatTask; }
                        catch (OperationCanceledException) { }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Driver Master sync queue poll failed.");
            }

            if (!processed)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task EnsureCanonicalWorkAsync(string workerInstanceId, bool includeStaleSync, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<TachoDriverMasterSyncJobService>();
        var recovered = await jobs.RecoverInterruptedAsync(workerInstanceId, ct);
        var quality = await scope.ServiceProvider.GetRequiredService<TachoDriverMasterSyncService>().QualityAsync(ct);
        var identityUnhealthy = quality.DuplicateMemberGroups > 0 || quality.DuplicateCardGroups > 0 || quality.ActiveWithoutMember > 0;
        var stale = quality.LatestCanonicalSyncUtc is null || quality.LatestCanonicalSyncUtc < DateTimeOffset.UtcNow.AddMinutes(-15);
        if (recovered > 0 || identityUnhealthy || (includeStaleSync && stale))
        {
            var actor = recovered > 0
                ? "system:tachomaster-canonical-driver-master-recovery"
                : "system:tachomaster-canonical-driver-master-startup";
            await jobs.EnqueueAsync(actor, ct);
        }
    }

    private async Task MaintainLeaseAsync(
        Guid jobId,
        string workerInstanceId,
        CancellationTokenSource jobCts,
        CancellationToken stoppingToken)
    {
        while (!jobCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, jobCts.Token);
                if (jobCts.IsCancellationRequested) return;

                await using var scope = scopeFactory.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<TachoDriverMasterSyncJobService>();
                if (await jobs.HeartbeatAsync(jobId, workerInstanceId, jobCts.Token)) continue;

                logger.LogWarning("Canonical Driver Master sync {JobId} lost worker lease {WorkerInstanceId}; cancelling local work.", jobId, workerInstanceId);
                jobCts.Cancel();
                return;
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested || stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not refresh canonical Driver Master worker lease {WorkerInstanceId}; cancelling local work to avoid concurrent cleanses.", workerInstanceId);
                jobCts.Cancel();
                return;
            }
        }
    }
}
