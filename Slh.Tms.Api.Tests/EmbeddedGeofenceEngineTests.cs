using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmbeddedGeofenceEngineTests
{
    [Fact]
    public void Operational_falcon_payload_loads_all_unique_run_fences_plus_approved_supplements()
    {
        var fences = EmbeddedGeofenceEngine.ApprovedFences;
        var expected = OperationalGeofencePayload.ExpectedFenceCount;

        Assert.Equal(expected, fences.Count);
        Assert.Equal(expected, fences.Select(x => x.Id).Distinct().Count());
        Assert.Equal(expected, fences.Select(x => x.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(fences, fence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(fence.Name));
            Assert.True(fence.Points.Count >= 3);
            Assert.All(fence.Points, point =>
            {
                Assert.InRange(point.Longitude, -180d, 180d);
                Assert.InRange(point.Latitude, -90d, 90d);
            });
        });
    }

    [Fact]
    public void Operational_falcon_payload_has_expected_category_counts_and_excludes_non_run_zones()
    {
        var fences = EmbeddedGeofenceEngine.ApprovedFences;
        var counts = fences.GroupBy(x => x.Category).ToDictionary(x => x.Key ?? string.Empty, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        Assert.Equal(180, counts["Service Station"]);
        Assert.Equal(174, counts["Collection"]);
        Assert.Equal(169, counts["Delivery"]);
        Assert.Equal(43, counts["Traywash"]);
        Assert.Equal(29, counts["NWF Collection"]);
        Assert.Equal(25, counts["Parking"]);
        Assert.Equal(21, counts["Restricted Access"]);
        Assert.Equal(20, counts["Reload Customer"]);
        Assert.Equal(18, counts["RDC"]);
        Assert.Equal(14, counts["Market Delivery"]);
        Assert.Equal(13, counts["Customer"]);
        Assert.Equal(10, counts["Service Centre"]);
        Assert.Equal(6, counts["DVS"]);
        Assert.Equal(5, counts["FWC Collection"]);
        Assert.Equal(4, counts["Roundstone Collection"]);
        Assert.Equal(4, counts["DVSA Checkpoint"]);
        Assert.Equal(1, counts["SLH DEPOT"]);
    }

    [Fact]
    public void Known_uploaded_fences_preserve_longitude_latitude_order()
    {
        var fences = EmbeddedGeofenceEngine.ApprovedFences;

        var aylesford = Assert.Single(fences.Where(x => x.Name.Trim() == "Aylesford (Waitrose)"));
        Assert.Equal("RDC", aylesford.Category);
        Assert.InRange(aylesford.Points[0].Longitude, 0.49d, 0.50d);
        Assert.InRange(aylesford.Points[0].Latitude, 51.30d, 51.31d);

        var bracknell = Assert.Single(fences.Where(x => x.Name.Trim() == "Bracknell Traywash"));
        Assert.Equal("Traywash", bracknell.Category);
        Assert.InRange(bracknell.Points[0].Longitude, -0.77d, -0.76d);
        Assert.InRange(bracknell.Points[0].Latitude, 51.41d, 51.42d);
    }
}
