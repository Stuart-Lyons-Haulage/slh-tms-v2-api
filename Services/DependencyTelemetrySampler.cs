namespace Slh.Tms.Api.Services;

public sealed class DependencyTelemetrySampler(
    IServiceScopeFactory scopeFactory,
    TmsMetrics metrics,
    ILogger<DependencyTelemetrySampler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var health = scope.ServiceProvider.GetRequiredService<DependencyHealthService>();
                var snapshot = await health.GetSnapshotAsync(stoppingToken);
                metrics.UpdateFreshness(
                    snapshot.Dependencies["RoadTech"].LastSuccessfulContactUtc,
                    snapshot.Dependencies["Fleetio"].LastSuccessfulContactUtc,
                    snapshot.Dependencies["TachoMaster"].LastSuccessfulContactUtc,
                    snapshot.Dependencies["Sage HR"].LastSuccessfulContactUtc);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dependency telemetry sampler failed; retaining the previous freshness observations.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
