using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class LegacyLiveRunsBoundedSourceTests
{
    [Fact]
    public void Legacy_live_runs_does_not_rebuild_geofences_or_call_maps_inline()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controllers", "TvDisplayController.cs"));
        var start = source.IndexOf("[HttpGet(\"live-runs\")", StringComparison.Ordinal);
        var end = source.IndexOf("internal static bool ShouldHideCompletedRun", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var method = source[start..end];
        Assert.DoesNotContain("EmbeddedGeofenceEngine.BuildAsync", method);
        Assert.DoesNotContain("EmbeddedGeofenceEvidenceMerge", method);
        Assert.DoesNotContain("maps.TravelTimeAsync", method);
        Assert.Contains("db.GeofenceVisits.AsNoTracking()", method);
        Assert.Contains("PlanningResilience.CollapseLogicalDuplicates", method);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Controllers"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }
}
