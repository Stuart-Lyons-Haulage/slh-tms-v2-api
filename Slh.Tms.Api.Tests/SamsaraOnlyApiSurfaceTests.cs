using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

/// <summary>
/// Deprecated vehicle tracking, ETA, Azure Maps and geofencing controllers must
/// not be accidentally restored after Samsara becomes the execution authority.
/// </summary>
public sealed class SamsaraOnlyApiSurfaceTests
{
    [Theory]
    [InlineData("GeofencesController")]
    [InlineData("DriverDispatchRouteController")]
    [InlineData("EtaController")]
    [InlineData("EtaLearningController")]
    [InlineData("EtaPrecisionController")]
    [InlineData("SiteGeofenceSyncController")]
    [InlineData("TrackingStateController")]
    [InlineData("TvDisplayRunLabelsController")]
    [InlineData("BetaOptimiserController")]
    [InlineData("FuelOptimisationController")]
    public void Retired_execution_controllers_are_not_part_of_TMS_API(string name)
    {
        var exposed = typeof(PlanningController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(name, exposed);
    }

    [Fact]
    public void Samsara_export_and_capacity_planning_controllers_are_preserved()
    {
        var types = typeof(PlanningController).Assembly.GetTypes().ToHashSet();
        Assert.Contains(typeof(PlanningController), types);
        Assert.Contains(typeof(RunAllocationResilienceController), types);
        Assert.Contains(typeof(SamsaraDispatchController), types);
    }
}
