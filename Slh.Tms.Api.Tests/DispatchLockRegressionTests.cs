using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DispatchLockRegressionTests
{
    [Fact]
    public void Dispatch_service_does_not_parallelise_ef_reads_on_the_scoped_db_context()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Services", "DispatchService.cs"));

        Assert.DoesNotContain("Task.WhenAll(profilesTask, dutiesTask, runsTask, vehiclesTask, trailersTask)", source);
        Assert.DoesNotContain("Task.WhenAll(profilesTask, dutiesTask)", source);
        Assert.DoesNotContain("Task.WhenAll(profilesTask, dutiesTask, runProfilesTask, liveTask, historyTask)", source);
        Assert.Contains("var profiles = await ReadDriverMasterProfilesAsync(drivers, ct);", source);
        Assert.Contains("var duties = await ReadDutiesAsync(request.PlanningDate, ct);", source);
        Assert.Contains("var runProfiles = await ReadRunProfilesAsync(request.PlanningDate, ct);", source);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Slh.Tms.Api.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
