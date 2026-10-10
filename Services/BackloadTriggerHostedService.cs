using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class BackloadTriggerHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<BackloadMatchingOptions> options,
    ILogger<BackloadTriggerHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(30);
    private readonly BackloadMatchingOptions _options = options.Value;
    private readonly TimeZoneInfo _ukZone = ResolveUkZone();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ProcessScheduledWindowAsync(stoppingToken);
                await Task.Delay(WatchInterval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task ProcessScheduledWindowAsync(CancellationToken ct)
    {
        var localNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), _ukZone);
        if (localNow.Hour < 14 || localNow.Hour >= 18) return;
        var planningDate = DateOnly.FromDateTime(localNow.DateTime);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var operations = scope.ServiceProvider.GetRequiredService<BackloadOperationsService>();
        var loads = await PlanningResilience.ReadLoadsAsync(db, planningDate, ct);
        var barnham = new MatrixPoint(_options.BarnhamLatitude, _options.BarnhamLongitude);

        foreach (var load in loads.Where(item => item.Status is not LoadStatus.Completed and not LoadStatus.Cancelled && item.VehicleId != null))
        {
            var key = $"backload-scheduled:{planningDate:yyyyMMdd}:{load.Id:N}";
            if (await db.StagedImports.AsNoTracking().AnyAsync(row => row.IdempotencyKey == key, ct)) continue;
            var finalStop = load.Stops.OrderBy(stop => stop.Sequence).LastOrDefault(stop => stop.Latitude != null && stop.Longitude != null);
            if (finalStop?.Latitude is null || finalStop.Longitude is null)
            {
                await WriteReceiptAsync(db, key, new { loadId = load.Id, skipped = true, reason = "final-stop-unmapped" }, ct);
                continue;
            }

            var finish = new MatrixPoint(finalStop.Latitude.Value, finalStop.Longitude.Value);
            var distanceFromBarnham = BackloadMatchingService.HaversineMiles(finish, barnham);
            if (distanceFromBarnham <= _options.ScheduledFinishDistanceMiles)
            {
                await WriteReceiptAsync(db, key, new { loadId = load.Id, skipped = true, reason = "finish-within-threshold", distanceFromBarnham }, ct);
                continue;
            }

            var notification = await operations.EvaluateLoadAsync(load.Id, finish, ct);
            await WriteReceiptAsync(db, key, new
            {
                loadId = load.Id,
                plannedFinish = finish,
                distanceFromBarnham,
                evaluatedAtUtc = timeProvider.GetUtcNow(),
                matchCount = notification?.Matches.Count ?? 0
            }, ct);
        }
    }

    private static async Task WriteReceiptAsync(TmsDbContext db, string key, object payload, CancellationToken ct)
    {
        db.StagedImports.Add(new StagedImport
        {
            EntityType = "backload-evaluation",
            IdempotencyKey = key,
            PayloadJson = JsonSerializer.Serialize(payload),
            Status = StagingStatus.Promoted,
            Source = "Backload matching engine",
            ReviewedAtUtc = DateTimeOffset.UtcNow,
            ReviewedBy = "TMS",
            ReviewNote = "Automated backload evaluation receipt"
        });
        await db.SaveChangesAsync(ct);
    }

    private static TimeZoneInfo ResolveUkZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }
}
