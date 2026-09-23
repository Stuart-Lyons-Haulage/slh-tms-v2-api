using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class DotTrackingIngestionService(IServiceScopeFactory scopeFactory, DotTrackingOptions options, ILogger<DotTrackingIngestionService> logger) : BackgroundService
{
    private const int MaximumHistoryRecoveryMinutes = 5;
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumHistoricalClockCorrection = TimeSpan.FromHours(48);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromMinutes(Math.Max(1, options.PollIntervalMinutes));
        var recoveryInterval = HistoryRecoveryInterval(options);
        var nextRecoveryAtUtc = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var client = scope.ServiceProvider.GetRequiredService<DotTrackingClient>();
                var store = scope.ServiceProvider.GetRequiredService<DotTrackingTelemetryStore>();
                var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
                var now = DateTimeOffset.UtcNow;
                var operatingDays = RecoveryDays(now);
                var projectionDays = new HashSet<DateOnly> { operatingDays[0] };

                // This is the only production path permitted to refresh Falcon current telemetry.
                // Every wallboard/API/dispatch consumer reads the same immutable snapshot captured here.
                var records = NormaliseCurrentEventTimes(
                    (await client.RefreshLatestVehicleEventsAsync(stoppingToken))
                        .Select(DotTelemetryRecord.FromProvider),
                    DateTimeOffset.UtcNow);
                await store.PersistAsync(records, stoppingToken, markAsLiveReceipt: true);
                await TryRepairProviderVehicleMappingsAsync(db, records.Select(record => record.VehicleIdentifier), "current", stoppingToken);

                // Live ENTER/EXIT detection is authoritative from the active SQL SiteGeofences
                // maintained through Site Master. The embedded payload remains a resilience-only
                // fallback when no active SQL geofence catalogue exists.
                try
                {
                    await GeofenceRunProgression.ProcessTelemetryAsync(db, records, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    db.ChangeTracker.Clear();
                    logger.LogWarning(exception, "Site Master geofence hit processing failed for current RoadTech telemetry; tracking ingestion will continue.");
                }

                // RoadTech retains historical journeys. Do not fetch or replay them into SQL;
                // current polls above create only operational enter/exit events.
            }
            catch (InvalidOperationException exception) { logger.LogDebug(exception, "DOT tracking ingestion is not configured."); }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(exception, "DOT tracking ingestion failed; retrying in {Minutes} minute(s).", pollInterval.TotalMinutes); }
            await Task.Delay(pollInterval, stoppingToken);
        }
    }

    internal static IReadOnlyList<DotTelemetryRecord> NormaliseCurrentEventTimes(
        IEnumerable<DotTelemetryRecord> source,
        DateTimeOffset receivedAtUtc)
    {
        var ceiling = receivedAtUtc.Add(MaximumFutureSkew);
        return source
            .Select(record => record.EventTimeUtc > ceiling
                ? record with { EventTimeUtc = receivedAtUtc }
                : record)
            .ToList();
    }

    internal static IReadOnlyList<DotTelemetryRecord> NormaliseHistoricalEventTimes(
        IEnumerable<DotTelemetryRecord> source,
        IReadOnlyCollection<DotTelemetryRecord> currentRecords,
        DateOnly recoveryDay,
        DateTimeOffset nowUtc)
    {
        var historical = source.ToList();
        if (historical.Count == 0) return historical;

        // Only today's historical page can be calibrated against GetCurrentTelemetry.
        // If Falcon history is systematically future-dated, the current fleet snapshot is
        // the authoritative clock anchor for the same vehicle. Shift that vehicle's entire
        // historical trail by the measured skew so ENTER/EXIT ordering is preserved rather
        // than collapsing every bad point onto receipt time.
        if (RecoveryDays(nowUtc)[0] != recoveryDay) return historical;

        var currentByVehicle = currentRecords
            .GroupBy(record => ExecutionIdentityResolver.NormaliseVehicle(record.VehicleIdentifier), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.Max(record => record.EventTimeUtc), StringComparer.OrdinalIgnoreCase);
        var futureCeiling = nowUtc.Add(MaximumFutureSkew);
        var result = new List<DotTelemetryRecord>(historical.Count);

        foreach (var group in historical.GroupBy(record => ExecutionIdentityResolver.NormaliseVehicle(record.VehicleIdentifier), StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.ToList();
            if (group.Key.Length == 0 || !currentByVehicle.TryGetValue(group.Key, out var currentTimeUtc))
            {
                result.AddRange(rows);
                continue;
            }

            var newestHistoricalUtc = rows.Max(record => record.EventTimeUtc);
            var skew = newestHistoricalUtc - currentTimeUtc;
            if (newestHistoricalUtc <= futureCeiling || skew <= MaximumFutureSkew || skew > MaximumHistoricalClockCorrection)
            {
                result.AddRange(rows);
                continue;
            }

            result.AddRange(rows.Select(record => record with { EventTimeUtc = record.EventTimeUtc - skew }));
        }

        return result;
    }

    private async Task TryRepairProviderVehicleMappingsAsync(
        TmsDbContext db,
        IEnumerable<string?> providerIdentifiers,
        string source,
        CancellationToken ct)
    {
        try
        {
            var repaired = await RepairProviderVehicleMappingsAsync(db, providerIdentifiers, ct);
            if (repaired > 0)
                logger.LogInformation("Learned {MappingCount} exact RoadTech vehicle key mapping(s) from {Source} telemetry before geofence replay.", repaired, source);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Identity learning is supplementary to GPS capture. A schema or matching
            // problem must not interrupt current telemetry; projection will remain fail-safe
            // and retry after the next poll/history recovery.
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "RoadTech vehicle identity learning failed for {Source}; tracking ingestion will continue.", source);
        }
    }

    internal static async Task<int> RepairProviderVehicleMappingsAsync(
        TmsDbContext db,
        IEnumerable<string?> providerIdentifiers,
        CancellationToken ct)
    {
        var identifiers = providerIdentifiers
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Select(identifier => identifier!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (identifiers.Count == 0) return 0;

        var vehicles = await db.Vehicles.AsNoTracking()
            .Where(vehicle => vehicle.Active)
            .ToListAsync(ct);
        if (vehicles.Count == 0) return 0;

        return await ExecutionIdentityResolver.RepairDotVehicleMappingsAsync(db, vehicles, identifiers, ct);
    }

    internal static TimeSpan HistoryRecoveryInterval(DotTrackingOptions options)
    {
        var pollMinutes = Math.Max(1, options.PollIntervalMinutes);
        var configuredMinutes = Math.Max(pollMinutes, options.RecoveryIntervalMinutes);
        return TimeSpan.FromMinutes(Math.Min(MaximumHistoryRecoveryMinutes, configuredMinutes));
    }

    internal static IReadOnlyList<DateOnly> RecoveryDays(DateTimeOffset utcNow)
    {
        DateOnly today;
        try
        {
            today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utcNow, "Europe/London").DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            today = DateOnly.FromDateTime(utcNow.UtcDateTime);
        }

        // Current day repairs any missed polling/persistence before the recovery run;
        // previous day preserves overnight duties that cross the operating-day boundary.
        return [today, today.AddDays(-1)];
    }
}
