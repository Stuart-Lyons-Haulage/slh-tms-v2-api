using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class NightlyArchiveOptions
{
    public bool Enabled { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public int AuditOutboxRetentionDays { get; set; } = 7;
    public int TelemetryRetentionDays { get; set; } = 90;
    public int GeofenceRetentionDays { get; set; } = 180;
    public int EtaRetentionDays { get; set; } = 90;
    public int SyncReceiptRetentionDays { get; set; } = 30;
    public int BatchSize { get; set; } = 500;
    public int MaxRowsPerTablePerRun { get; set; } = 10000;
}

public sealed class NightlyArchiveBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<NightlyArchiveOptions> options,
    ILogger<NightlyArchiveBackgroundService> logger) : BackgroundService
{
    private const string ReadyMarker = "SLH_TMS_ARCHIVE_READY.txt";
    private DateOnly? _lastLocalRun;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var local = London(now);
                var localDate = DateOnly.FromDateTime(local.DateTime);
                if (options.Value.Enabled && local.Hour >= 2 && _lastLocalRun != localDate)
                {
                    await RunOnceAsync(localDate, stoppingToken);
                    _lastLocalRun = localDate;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Nightly archive cycle failed. No unverified archive batch is eligible for deletion.");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    private async Task RunOnceAsync(DateOnly localDate, CancellationToken ct)
    {
        var cfg = options.Value;
        var root = cfg.RootPath?.Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            logger.LogWarning("Nightly archive is enabled but Archive:RootPath is empty; skipping.");
            return;
        }

        var marker = Path.Combine(root, ReadyMarker);
        if (!File.Exists(marker))
        {
            logger.LogWarning("Nightly archive root {RootPath} is not armed. Marker {Marker} is missing; no rows will be deleted.", root, ReadyMarker);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var leases = scope.ServiceProvider.GetRequiredService<DistributedLeaseManager>();
        await using var lease = await leases.TryAcquireAsync("maintenance:nightly-archive", TimeSpan.FromHours(2), ct);
        if (lease is null) return;

        var dayRoot = Path.Combine(root, localDate.ToString("yyyy"), localDate.ToString("MM"), localDate.ToString("dd"));
        Directory.CreateDirectory(dayRoot);

        var now = DateTimeOffset.UtcNow;
        await ArchiveAuditOutboxAsync(db, dayRoot, now.AddDays(-Math.Max(1, cfg.AuditOutboxRetentionDays)), cfg, ct);
        await ArchiveTrackingAsync(db, dayRoot, now.AddDays(-Math.Max(7, cfg.TelemetryRetentionDays)), cfg, ct);
        await ArchiveGeofenceVisitsAsync(db, dayRoot, now.AddDays(-Math.Max(30, cfg.GeofenceRetentionDays)), cfg, ct);
        await ArchiveEtaAsync(db, dayRoot, now.AddDays(-Math.Max(7, cfg.EtaRetentionDays)), cfg, ct);
        await ArchiveSyncReceiptsAsync(db, dayRoot, now.AddDays(-Math.Max(7, cfg.SyncReceiptRetentionDays)), cfg, ct);
    }

    private async Task ArchiveAuditOutboxAsync(TmsDbContext db, string root, DateTimeOffset cutoff, NightlyArchiveOptions cfg, CancellationToken ct)
    {
        await ArchiveInBatchesAsync(
            "AuditOutbox",
            root,
            cfg,
            async take => await db.AuditOutboxes.AsNoTracking()
                .Where(x => x.ProcessedAt != null && x.ProcessedAt < cutoff)
                .OrderBy(x => x.ProcessedAt).Take(take).ToListAsync(ct),
            rows => rows.Select(x => x.OutboxId).ToArray(),
            async ids => await db.AuditOutboxes.Where(x => ids.Contains(x.OutboxId)).ExecuteDeleteAsync(ct),
            ct);
    }

    private async Task ArchiveTrackingAsync(TmsDbContext db, string root, DateTimeOffset cutoff, NightlyArchiveOptions cfg, CancellationToken ct)
    {
        await ArchiveInBatchesAsync(
            "VehicleTrackingEvents",
            root,
            cfg,
            async take => await db.VehicleTrackingEvents.AsNoTracking()
                .Where(x => x.EventTimeUtc < cutoff).OrderBy(x => x.EventTimeUtc).Take(take).ToListAsync(ct),
            rows => rows.Select(x => x.Id).ToArray(),
            async ids => await db.VehicleTrackingEvents.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct),
            ct);
    }

    private async Task ArchiveGeofenceVisitsAsync(TmsDbContext db, string root, DateTimeOffset cutoff, NightlyArchiveOptions cfg, CancellationToken ct)
    {
        await ArchiveInBatchesAsync(
            "GeofenceVisits",
            root,
            cfg,
            async take => await db.GeofenceVisits.AsNoTracking()
                .Where(x => x.UpdatedAtUtc < cutoff).OrderBy(x => x.UpdatedAtUtc).Take(take).ToListAsync(ct),
            rows => rows.Select(x => x.Id).ToArray(),
            async ids => await db.GeofenceVisits.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct),
            ct);
    }

    private async Task ArchiveEtaAsync(TmsDbContext db, string root, DateTimeOffset cutoff, NightlyArchiveOptions cfg, CancellationToken ct)
    {
        await ArchiveInBatchesAsync(
            "EtaSnapshots",
            root,
            cfg,
            async take => await db.EtaSnapshots.AsNoTracking()
                .Where(x => x.CapturedAtUtc < cutoff).OrderBy(x => x.CapturedAtUtc).Take(take).ToListAsync(ct),
            rows => rows.Select(x => x.Id).ToArray(),
            async ids => await db.EtaSnapshots.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct),
            ct);
    }

    private async Task ArchiveSyncReceiptsAsync(TmsDbContext db, string root, DateTimeOffset cutoff, NightlyArchiveOptions cfg, CancellationToken ct)
    {
        var disposableTypes = new[] { "sagehrsync", "tachomastersync", "fleetiosync", "driverreview" };
        await ArchiveInBatchesAsync(
            "IntegrationReceipts",
            root,
            cfg,
            async take => await db.StagedImports.AsNoTracking()
                .Where(x => disposableTypes.Contains(x.EntityType)
                    && x.Status != StagingStatus.PendingReview
                    && x.Status != StagingStatus.Approved
                    && (x.ReviewedAtUtc ?? x.ReceivedAtUtc) < cutoff
                    && !db.OrderRevisions.Any(r => r.StagedImportId == x.Id)
                    && !db.StagedImportEvents.Any(e => e.StagedImportId == x.Id))
                .OrderBy(x => x.ReviewedAtUtc ?? x.ReceivedAtUtc)
                .Take(take).ToListAsync(ct),
            rows => rows.Select(x => x.Id).ToArray(),
            async ids => await db.StagedImports.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct),
            ct);
    }

    private async Task ArchiveInBatchesAsync<T, TKey>(
        string category,
        string root,
        NightlyArchiveOptions cfg,
        Func<int, Task<List<T>>> read,
        Func<List<T>, TKey[]> keys,
        Func<TKey[], Task<int>> delete,
        CancellationToken ct)
        where TKey : notnull
    {
        var total = 0;
        var limit = Math.Max(1, cfg.MaxRowsPerTablePerRun);
        var batchSize = Math.Clamp(cfg.BatchSize, 10, 2000);

        while (total < limit && !ct.IsCancellationRequested)
        {
            var rows = await read(Math.Min(batchSize, limit - total));
            if (rows.Count == 0) break;

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff");
            var folder = Path.Combine(root, category);
            Directory.CreateDirectory(folder);
            var finalPath = Path.Combine(folder, $"{category}-{stamp}-{Guid.NewGuid():N}.jsonl.gz");
            var tempPath = finalPath + ".tmp";

            await WriteVerifiedArchiveAsync(tempPath, finalPath, rows, ct);

            var ids = keys(rows);
            var deleted = await delete(ids);
            if (deleted != rows.Count)
                throw new InvalidOperationException($"Archive delete count mismatch for {category}: archived {rows.Count}, deleted {deleted}.");

            total += deleted;
            logger.LogInformation("Archived and removed {Count} {Category} row(s) to {ArchivePath}.", deleted, category, finalPath);
        }
    }

    private static async Task WriteVerifiedArchiveAsync<T>(string tempPath, string finalPath, IReadOnlyCollection<T> rows, CancellationToken ct)
    {
        await using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: false))
        await using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(row));
            }
        }

        var expected = await HashFileAsync(tempPath, ct);
        File.Move(tempPath, finalPath, overwrite: false);
        var actual = await HashFileAsync(finalPath, ct);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new IOException($"Archive checksum verification failed for {finalPath}.");

        await File.WriteAllTextAsync(finalPath + ".sha256", Convert.ToHexString(actual), ct);
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return await SHA256.HashDataAsync(stream, ct);
    }

    private static DateTimeOffset London(DateTimeOffset utc)
    {
        try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")); }
        catch (TimeZoneNotFoundException)
        {
            try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time")); }
            catch (TimeZoneNotFoundException) { return utc; }
        }
    }
}
