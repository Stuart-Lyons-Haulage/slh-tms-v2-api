using Microsoft.Extensions.Logging;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Jobs;

public sealed record JobExecutionResult(bool Success, string Message, int Changed = 0);

public sealed class ScheduledJobRunner(DistributedLeaseManager leases, ILogger<ScheduledJobRunner> logger)
{
    public async Task<int> RunAsync(
        string jobName,
        string leaseId,
        TimeSpan leaseDuration,
        Func<CancellationToken, Task<JobExecutionResult>> action,
        CancellationToken ct)
    {
        var instanceId = $"{Environment.MachineName}:{Environment.ProcessId}";
        var started = DateTimeOffset.UtcNow;

        try
        {
            await using var lease = await leases.TryAcquireAsync(leaseId, leaseDuration, ct);
            if (lease is null)
            {
                logger.LogInformation(
                    "ScheduledJobSkippedLeaseHeld JobName={JobName} LeaseId={LeaseId} InstanceId={InstanceId}",
                    jobName, leaseId, instanceId);
                return 0;
            }

            logger.LogInformation(
                "ScheduledJobStarted JobName={JobName} LeaseId={LeaseId} InstanceId={InstanceId} StartedAtUtc={StartedAtUtc}",
                jobName, leaseId, instanceId, started);

            var result = await action(ct);
            if (!result.Success) throw new InvalidOperationException(result.Message);

            logger.LogInformation(
                "ScheduledJobCompleted JobName={JobName} LeaseId={LeaseId} InstanceId={InstanceId} StartedAtUtc={StartedAtUtc} CompletedAtUtc={CompletedAtUtc} Changed={Changed} Message={Message}",
                jobName, leaseId, instanceId, started, DateTimeOffset.UtcNow, result.Changed, result.Message);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "ScheduledJobCancelled JobName={JobName} LeaseId={LeaseId} InstanceId={InstanceId} StartedAtUtc={StartedAtUtc} CancelledAtUtc={CancelledAtUtc}",
                jobName, leaseId, instanceId, started, DateTimeOffset.UtcNow);
            return 2;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "ScheduledJobFailed JobName={JobName} LeaseId={LeaseId} InstanceId={InstanceId} StartedAtUtc={StartedAtUtc} FailedAtUtc={FailedAtUtc}",
                jobName, leaseId, instanceId, started, DateTimeOffset.UtcNow);
            return 1;
        }
    }
}
