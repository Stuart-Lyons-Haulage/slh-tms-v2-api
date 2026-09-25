using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoMasterDriverMasterRegressionTests
{
    [Fact]
    public void Scheduled_tacho_job_is_feed_only_and_does_not_create_or_canonicalise_drivers()
    {
        var source = ReadRepoFile("Slh.Tms.Jobs/TachoMasterScheduledJob.cs");

        Assert.Contains("SyncTachoMasterCoreAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TachoObservedDriverSyncService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TachoCanonicalDriverMasterOrchestrator", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DistributedLeaseManager", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryAcquireAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Tacho_health_does_not_require_multiple_active_result_sets()
    {
        var source = ReadRepoFile("Controllers/TachoMasterHealthController.cs");

        Assert.DoesNotContain("DistributedLeaseManager", source, StringComparison.Ordinal);
        Assert.DoesNotContain("leaseTask", source, StringComparison.Ordinal);
        Assert.Contains("await Task.WhenAll(profilesTask, openDutiesTask, dayDutiesTask)", source, StringComparison.Ordinal);
        Assert.Contains("await db.Drivers.AsNoTracking()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Tacho_driver_enrichment_prefers_member_code_and_does_not_insert_driver_rows()
    {
        var source = ReadRepoFile("Services/IntegrationSyncCoordinator.cs");
        var start = source.IndexOf("public async Task<IntegrationSyncResult> SyncTachoMasterCoreAsync", StringComparison.Ordinal);
        var end = source.IndexOf("public async Task<IntegrationSyncResult> SyncSageHrAsync", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var tachoSync = source[start..end];
        Assert.Contains("byMemberCode", tachoSync, StringComparison.Ordinal);
        Assert.Contains("driver.TachoMasterDriverId", tachoSync, StringComparison.Ordinal);
        Assert.Contains("Identity order: member code, tacho card, employee number, name", tachoSync, StringComparison.Ordinal);
        Assert.Contains("memberOwnerIds", tachoSync, StringComparison.Ordinal);
        Assert.Contains("Skipping TachoMaster member", tachoSync, StringComparison.Ordinal);
        Assert.Contains("conflicting member assignment(s) were skipped safely", tachoSync, StringComparison.Ordinal);
        Assert.DoesNotContain("db.Drivers.Add", tachoSync, StringComparison.Ordinal);
        Assert.DoesNotContain("new Driver", tachoSync, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Slh.Tms.Api.csproj")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Expected repository file was not found: {path}");
        return File.ReadAllText(path);
    }
}
