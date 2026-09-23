using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class NaturesWayPlanningMatchTests
{
    [Theory]
    [InlineData("Drayton")]
    [InlineData("Runcton")]
    [InlineData("Merston")]
    [InlineData("Selsey")]
    public void Nwf_planner_locality_resolves_to_one_approved_physical_fence(string locality)
    {
        var plannerLabel = $"NWF-{locality}";
        var stop = new LoadStop { Id = Guid.NewGuid(), Sequence = 1, Name = plannerLabel };

        var matchingFences = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => GeofencePlanningMatch.SamePhysicalSite(stop, fence))
            .ToList();

        var fence = Assert.Single(matchingFences);
        Assert.Equal(fence.Name, GeofencePlanningMatch.MatchText(plannerLabel));
    }

    [Theory]
    [InlineData("Collect · NWF-Selsey", "Selsey")]
    [InlineData("Collection · NWF-Merston", "Merston")]
    [InlineData("Collect NWF-Drayton", "Drayton")]
    [InlineData("Collect · NWF-Runcton", "Runcton")]
    public void Source_line_operational_prefix_does_not_change_nwf_physical_site(string plannerLabel, string locality)
    {
        var stop = new LoadStop { Id = Guid.NewGuid(), Sequence = 1, Name = plannerLabel };
        var matchingFences = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => GeofencePlanningMatch.SamePhysicalSite(stop, fence))
            .ToList();

        var fence = Assert.Single(matchingFences);
        Assert.Contains(locality, fence.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fence.Name, GeofencePlanningMatch.MatchText(plannerLabel));
    }

    [Fact]
    public void Explicit_nwf_source_label_wins_over_conflicting_import_coordinate()
    {
        var merstonFence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => string.Equals(fence.Name, GeofencePlanningMatch.MatchText("NWF-Merston"), StringComparison.OrdinalIgnoreCase)));
        var wrongCoordinate = Assert.NotNull(OperationalRunOrigin.FenceCentre(merstonFence));

        var load = new Load
        {
            Id = Guid.NewGuid(),
            Reference = "Run 99 AM",
            PlanningDate = new DateOnly(2026, 8, 26),
            Stops =
            [
                new LoadStop
                {
                    Id = Guid.NewGuid(),
                    Sequence = 1,
                    Name = "Collect · NWF-Selsey",
                    Longitude = wrongCoordinate.Longitude,
                    Latitude = wrongCoordinate.Latitude
                }
            ]
        };

        var prepared = GeofencePlanningMatch.PrepareLoad(load);

        Assert.Equal(GeofencePlanningMatch.MatchText("NWF-Selsey"), prepared.Stops.Single().Name);
    }
}
