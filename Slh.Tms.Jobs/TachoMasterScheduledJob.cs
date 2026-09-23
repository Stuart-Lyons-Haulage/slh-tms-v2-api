using Slh.Tms.Api.Services;

namespace Slh.Tms.Jobs;

public sealed class TachoMasterScheduledJob(IntegrationSyncCoordinator integration)
{
    public async Task<JobExecutionResult> RunAsync(CancellationToken ct)
    {
        // The Container Apps job runner already serialises this scheduled execution.
        // TachoMaster is a feed into the authoritative SQL Driver Master: it enriches
        // existing drivers only and must not create, archive or canonicalise Driver Master rows.
        var sync = await integration.SyncTachoMasterCoreAsync("system:aca-job:tachomaster", ct);
        return new JobExecutionResult(sync.Success, sync.Message, sync.Changed);
    }
}
