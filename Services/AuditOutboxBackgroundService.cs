using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class AuditOutboxBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditOutboxBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = new AuditOutboxProcessor(
                    scope.ServiceProvider.GetRequiredService<TmsDbContext>(),
                    scope.ServiceProvider.GetRequiredService<ILogger<AuditOutboxProcessor>>());
                await processor.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit outbox polling cycle failed. The worker will retry on the next cycle.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

public sealed class AuditOutboxProcessor(
    TmsDbContext db,
    ILogger<AuditOutboxProcessor> logger)
{
    internal const int MaximumRetries = 5;
    internal const int BatchSize = 50;
    internal static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

    public async Task<int> ProcessPendingAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var staleCutoff = now - StaleThreshold;

        var stale = await db.AuditOutboxes
            .AsNoTracking()
            .Where(x => x.ProcessedAt == null && x.FailedAt == null && x.CreatedAt <= staleCutoff)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { x.OutboxId, x.CreatedAt, x.RetryCount })
            .FirstOrDefaultAsync(ct);

        if (stale is not null)
        {
            logger.LogWarning(
                "Audit outbox contains an unprocessed event older than five minutes. OutboxId={OutboxId}, CreatedAt={CreatedAt}, RetryCount={RetryCount}.",
                stale.OutboxId,
                stale.CreatedAt,
                stale.RetryCount);
        }

        var pending = await db.AuditOutboxes
            .AsNoTracking()
            .Where(x => x.ProcessedAt == null && x.FailedAt == null)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { x.OutboxId })
            .Take(BatchSize)
            .ToListAsync(ct);

        var processed = 0;
        foreach (var item in pending)
        {
            if (await ProcessOneAsync(item.OutboxId, ct))
                processed++;
        }

        return processed;
    }

    private async Task<bool> ProcessOneAsync(Guid outboxId, CancellationToken ct)
    {
        var item = await db.AuditOutboxes.SingleOrDefaultAsync(x => x.OutboxId == outboxId, ct);
        if (item is null || item.ProcessedAt is not null || item.FailedAt is not null)
            return false;

        Guid? auditId = null;

        try
        {
            if (!string.Equals(item.EventType, AuditOutboxEventTypes.MasterDataAudit, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsupported audit outbox event type '{item.EventType}'.");

            var audit = JsonSerializer.Deserialize<MasterDataAudit>(item.Payload)
                ?? throw new InvalidOperationException("Audit outbox payload deserialised to null.");
            auditId = audit.Id;

            var alreadyWritten = await db.MasterDataAudits
                .AsNoTracking()
                .AnyAsync(x => x.Id == audit.Id, ct);

            if (!alreadyWritten)
                db.MasterDataAudits.Add(audit);

            item.ProcessedAt = DateTimeOffset.UtcNow;
            await db.SaveAuditReplayChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Audit outbox event {OutboxId} failed to replay.", outboxId);
            db.ChangeTracker.Clear();

            if (auditId.HasValue && await db.MasterDataAudits.AsNoTracking().AnyAsync(x => x.Id == auditId.Value, ct))
            {
                var concurrentlyProcessed = await db.AuditOutboxes.SingleOrDefaultAsync(x => x.OutboxId == outboxId, ct);
                if (concurrentlyProcessed is not null && concurrentlyProcessed.ProcessedAt is null)
                {
                    concurrentlyProcessed.ProcessedAt = DateTimeOffset.UtcNow;
                    await db.SaveAuditReplayChangesAsync(ct);
                }
                return true;
            }

            var failed = await db.AuditOutboxes.SingleOrDefaultAsync(x => x.OutboxId == outboxId, ct);
            if (failed is null || failed.ProcessedAt is not null || failed.FailedAt is not null)
                return false;

            failed.RetryCount++;
            if (failed.RetryCount >= MaximumRetries)
            {
                failed.FailedAt = DateTimeOffset.UtcNow;
                logger.LogError(
                    "Audit outbox event {OutboxId} reached the retry limit ({RetryCount}) and has been marked failed.",
                    failed.OutboxId,
                    failed.RetryCount);
            }

            await db.SaveAuditReplayChangesAsync(ct);
            return false;
        }
    }
}
