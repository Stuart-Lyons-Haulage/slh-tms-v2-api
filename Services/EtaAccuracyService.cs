using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public sealed record EtaAccuracyReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int TotalNotifications,
    int ConfirmedArrivals,
    decimal? MeanAbsoluteVarianceMinutes,
    decimal? Within15MinutesPercent,
    decimal? Within30MinutesPercent,
    bool CustomerNotificationsEnabled,
    string? SuppressionReason);

public sealed class EtaAccuracyProcessor(
    TmsDbContext db,
    TelemetryClient telemetry,
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    public async Task<int> ReconcileAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var pending = await CustomerNotificationStore.PendingAccuracyAsync(db, now.AddMinutes(-30), ct);
        var updated = 0;
        foreach (var log in pending)
        {
            var arrival = await db.GeofenceVisits.AsNoTracking()
                .Where(visit => visit.LoadStopId == log.StopId && visit.ConfirmedAtUtc != null && visit.EnteredAtUtc >= log.SentAtUtc.AddHours(-2) && visit.EnteredAtUtc <= log.SentAtUtc.AddDays(2))
                .OrderBy(visit => visit.EnteredAtUtc)
                .Select(visit => (DateTimeOffset?)visit.EnteredAtUtc)
                .FirstOrDefaultAsync(ct);
            if (arrival is null) continue;

            var promisedEta = await db.EtaSnapshots.AsNoTracking()
                .Where(snapshot => snapshot.StopId == log.StopId && snapshot.EtaUtc != null && snapshot.CapturedAtUtc <= log.SentAtUtc)
                .OrderByDescending(snapshot => snapshot.CapturedAtUtc)
                .Select(snapshot => snapshot.EtaUtc)
                .FirstOrDefaultAsync(ct);
            int? variance = promisedEta is null
                ? null
                : (int)Math.Round((arrival.Value - promisedEta.Value).TotalMinutes, MidpointRounding.AwayFromZero);

            await CustomerNotificationStore.UpdateActualAsync(db, log.Id, arrival.Value, variance, ct);
            if (variance is int minutes)
                telemetry.TrackMetric("EtaVarianceMinutes", minutes);
            updated++;
        }
        return updated;
    }

    public async Task<EtaAccuracyReport> ReportAsync(CancellationToken ct)
    {
        var toUtc = timeProvider.GetUtcNow();
        var fromUtc = toUtc.AddDays(-28);
        var logs = await CustomerNotificationStore.ListAsync(db, null, fromUtc, toUtc, ct);
        var variances = logs.Where(log => log.ActualVarianceMinutes != null).Select(log => Math.Abs(log.ActualVarianceMinutes!.Value)).ToList();
        decimal? mean = variances.Count == 0 ? null : Math.Round((decimal)variances.Average(), 1);
        decimal? within15 = variances.Count == 0 ? null : Math.Round(variances.Count(value => value <= 15) * 100m / variances.Count, 1);
        decimal? within30 = variances.Count == 0 ? null : Math.Round(variances.Count(value => value <= 30) * 100m / variances.Count, 1);
        var enabled = configuration.GetValue("CustomerNotifications:Enabled", false);
        var suppression = enabled ? null : configuration["CustomerNotifications:SuppressionReason"] ?? "Customer notifications are disabled by configuration.";
        return new EtaAccuracyReport(fromUtc, toUtc, logs.Count, variances.Count, mean, within15, within30, enabled, suppression);
    }
}

public sealed class EtaAccuracyService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<EtaAccuracyService> logger) : BackgroundService
{
    private readonly TimeZoneInfo _ukZone = ResolveUkZone();
    private DateOnly? _lastRunDate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var localNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), _ukZone);
                var date = DateOnly.FromDateTime(localNow.DateTime);
                if (localNow.Hour >= 2 && _lastRunDate != date)
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var processor = scope.ServiceProvider.GetRequiredService<EtaAccuracyProcessor>();
                        var updated = await processor.ReconcileAsync(stoppingToken);
                        _lastRunDate = date;
                        logger.LogInformation("Nightly ETA accuracy reconciliation updated {UpdatedCount} customer notification record(s).", updated);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Nightly ETA accuracy reconciliation failed.");
                    }
                }
                await Task.Delay(TimeSpan.FromMinutes(15), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private static TimeZoneInfo ResolveUkZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }
}
