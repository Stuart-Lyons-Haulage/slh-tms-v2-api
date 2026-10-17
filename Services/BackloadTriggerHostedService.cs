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
                await ProcessGeofenceDeparturesAsync(stoppingToken);
                await ProcessScheduledWindowAsync(stoppingToken);
                await Task.Delay(WatchInterval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task ProcessGeofenceDeparturesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var operations = scope.ServiceProvider.GetRequiredService<BackloadOperationsService>();
        var since = timeProvider.GetUtcNow().AddHours(-2);
        var visits = await db.GeofenceVisits.AsNoTracking()
            .Where(visit => visit.Status == "Departed" && visit.ConfirmedAtUtc != null && visit.ExitedAtUtc >= since && visit.LoadId != null && visit.LoadStopId != null)
            .OrderBy(visit => visit.ExitedAtUtc)
            .Take(100)
            .ToListAsync(ct);

        foreach (var visit in visits)
        {
            var key = $"backload-geofence:{visit.Id:N}";
            if (await db.StagedImports.AsNoTracking().AnyAsync(row => row.IdempotencyKey == key, ct)) continue;

            var load = await PlanningRegisterStore.GetLoadAsync(db, visit.LoadId!.Value, ct)
                ?? await db.Loads.AsNoTracking().Include(item => item.Stops).SingleOrDefaultAsync(item => item.Id == visit.LoadId.Value, ct);
            if (load is null || !IsDeliveryStop(load, visit.LoadStopId!.Value))
            {
                await WriteReceiptAsync(db, key, new { visit.Id, skipped = true, reason = "not-delivery-stop" }, ct);
                continue;
            }

            var live = await db.VehicleLiveStatuses.AsNoTracking()
                .OrderByDescending(item => item.LastEventTimeUtc)
                .FirstOrDefaultAsync(item => item.VehicleIdentifier == visit.VehicleIdentifier, ct);
            if (live is null)
            {
                await WriteReceiptAsync(db, key, new { visit.Id, skipped = true, reason = "live-position-unavailable" }, ct);
                continue;
            }

            var notification = await operations.EvaluateLoadAsync(load.Id, new MatrixPoint(live.Latitude, live.Longitude), ct);
            await WriteReceiptAsync(db, key, new
            {
                visit.Id,
                loadId = load.Id,
                evaluatedAtUtc = timeProvider.GetUtcNow(),
                matchCount = notification?.Matches.Count ?? 0
            }, ct);
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

    private static bool IsDeliveryStop(Load load, Guid stopId)
    {
        var stop = load.Stops.SingleOrDefault(item => item.Id == stopId);
        if (stop?.OrderId is not Guid orderId) return false;
        return stop.Sequence == load.Stops.Where(item => item.OrderId == orderId).Max(item => item.Sequence);
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
