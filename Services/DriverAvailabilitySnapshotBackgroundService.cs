using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class DriverAvailabilitySnapshotBackgroundService(
    IServiceScopeFactory scopeFactory,
    SageHrClient sageHr,
    ILogger<DriverAvailabilitySnapshotBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
                for (var offset = 0; offset < 3; offset++)
                {
                    var date = today.AddDays(offset);
                    var snapshot = await DriverAvailabilityService.ReadAsync(db, date, stoppingToken, sageHr, logger);
                    var row = await db.DriverAvailabilitySnapshotRecords.SingleOrDefaultAsync(x => x.PlanningDate == date, stoppingToken);
                    row ??= new DriverAvailabilitySnapshotRecord { PlanningDate = date, Source = "Driver Master + SageHR + Tacho" };
                    row.GeneratedAtUtc = snapshot.GeneratedAtUtc;
                    row.PayloadJson = JsonSerializer.Serialize(snapshot);
                    row.StoredAtUtc = DateTimeOffset.UtcNow;
                    if (db.Entry(row).State == EntityState.Detached) db.DriverAvailabilitySnapshotRecords.Add(row);
                }
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Driver availability snapshot refresh failed; last known good snapshots remain available."); }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
