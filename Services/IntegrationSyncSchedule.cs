namespace Slh.Tms.Api.Services;

public static class IntegrationSyncSchedule
{
    private static readonly TimeZoneInfo UkTimeZone = ResolveUkTimeZone();

    public static DateTimeOffset NextFleetio(DateTimeOffset nowUtc)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, UkTimeZone);
        var nextLocal = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);
        return ToUtc(nextLocal);
    }

    public static DateTimeOffset NextSageHr(DateTimeOffset nowUtc)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, UkTimeZone);
        foreach (var time in new[] { new TimeOnly(5, 0), new TimeOnly(14, 0) })
        {
            var candidate = local.Date.Add(time.ToTimeSpan());
            if (candidate > local.DateTime)
                return ToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified));
        }

        return ToUtc(DateTime.SpecifyKind(local.Date.AddDays(1).AddHours(5), DateTimeKind.Unspecified));
    }

    private static DateTimeOffset ToUtc(DateTime local)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, UkTimeZone);
        return new DateTimeOffset(utc);
    }

    private static TimeZoneInfo ResolveUkTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }
}

public sealed class IntegrationSyncSchedulerBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<IntegrationSyncSchedulerBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Automatic integration sync schedules enabled: Fleetio hourly; Sage HR daily at 05:00 and 14:00 Europe/London.");
        await Task.WhenAll(RunFleetioScheduleAsync(stoppingToken), RunSageScheduleAsync(stoppingToken));
    }

    private async Task RunFleetioScheduleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await DelayUntilAsync(IntegrationSyncSchedule.NextFleetio(DateTimeOffset.UtcNow), ct);
            if (ct.IsCancellationRequested) break;
            await RunAsync("Fleetio", (coordinator, actor) => coordinator.SyncFleetioAsync(actor, ct), ct);
        }
    }

    private async Task RunSageScheduleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await DelayUntilAsync(IntegrationSyncSchedule.NextSageHr(DateTimeOffset.UtcNow), ct);
            if (ct.IsCancellationRequested) break;
            await RunAsync("Sage HR", (coordinator, actor) => coordinator.SyncSageHrAsync(actor, ct), ct);
        }
    }

    private async Task RunAsync(string provider, Func<IntegrationSyncCoordinator, string, Task<IntegrationSyncResult>> sync, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<IntegrationSyncCoordinator>();
            var result = await sync(coordinator, $"system:{provider.ToLowerInvariant().Replace(' ', '-')}-scheduled");
            if (result.Success)
                logger.LogInformation("Scheduled {Provider} sync completed: {Message}", provider, result.Message);
            else
                logger.LogWarning("Scheduled {Provider} sync did not complete successfully: {Message}", provider, result.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Scheduled {Provider} sync failed; the next scheduled pass will retry.", provider);
        }
    }

    private static async Task DelayUntilAsync(DateTimeOffset nextUtc, CancellationToken ct)
    {
        var delay = nextUtc - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);
    }
}
