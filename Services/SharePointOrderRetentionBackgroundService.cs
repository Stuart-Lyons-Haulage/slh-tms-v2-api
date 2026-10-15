using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Moves retained email attachment bytes out of Azure SQL after the 72-hour review window.
/// SQL metadata is retained, and failed or unlinked evidence remains retryable in SQL.
/// </summary>
public sealed class SharePointOrderRetentionBackgroundService(
    IServiceScopeFactory scopeFactory,
    SharePointOrderArchiveOptions options,
    ILogger<SharePointOrderRetentionBackgroundService> logger) : BackgroundService
{
    public static readonly TimeSpan RetentionAge = TimeSpan.FromHours(72);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private const int BatchSize = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured)
        {
            logger.LogInformation("SharePoint order attachment retention is disabled, not configured, or SQL payload cleanup is disabled; SQL evidence will be retained.");
            return;
        }

        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ArchiveExpiredEvidenceAsync(stoppingToken);
                await ArchiveCompletedOrdersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "SharePoint retention pass failed; eligible records will be retried."); }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ArchiveCompletedOrdersAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var archive = scope.ServiceProvider.GetRequiredService<SharePointOrderArchiveService>();
        await archive.ArchiveOrdersOlderThan72Hours(ct);
    }

    private async Task ArchiveExpiredEvidenceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var archive = scope.ServiceProvider.GetRequiredService<SharePointOrderArchiveService>();
        var cutoff = DateTimeOffset.UtcNow - RetentionAge;
        var expired = await db.StagedImports
            .Where(item => item.EntityType == "email-evidence" && item.ReceivedAtUtc <= cutoff && item.PayloadJson.Contains("contentBase64"))
            .OrderBy(item => item.ReceivedAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var evidence in expired)
        {
            if (ct.IsCancellationRequested) break;
            try { await archive.ArchiveExpiredEvidenceAsync(evidence, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Expired source evidence {EvidenceId} remains in SQL and will be retried.", evidence.Id); }
        }
    }
}
