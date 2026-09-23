using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationalGeofencePayloadTests
{
    [Fact]
    public void Embedded_operational_payload_loads_all_approved_geofences()
    {
        var fences = EmbeddedGeofenceEngine.ApprovedFences;

        Assert.Equal(OperationalGeofencePayload.ExpectedFenceCount, fences.Count);
        Assert.Contains(fences, fence => fence.Name == "Selsey (Natures Way)");
        Assert.Contains(fences, fence => fence.Name == "Merston (Natures Way)");
        Assert.Contains(fences, fence => fence.Name == "Drayton (Natures Way)");
        Assert.Contains(fences, fence => fence.Name == "Runcton (Natures Way)");
        Assert.Contains(fences, fence => fence.Name == "Vitacress Runcton");
        Assert.All(fences, fence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(fence.Name));
            Assert.True(fence.Points.Count >= 3, $"{fence.Name} has an invalid polygon.");
        });
    }
}
