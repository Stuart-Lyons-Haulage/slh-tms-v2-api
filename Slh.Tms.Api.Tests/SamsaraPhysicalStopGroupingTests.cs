using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SamsaraPhysicalStopGroupingTests
{
    [Fact]
    public void Compound_delivery_name_resolves_the_physical_destination_site()
    {
        var site = new Site
        {
            ExternalCode = "SITE152",
            Name = "Aldi-Atherstone",
            DriverTextName = "Aldi-Atherstone",
            CollectionAddress = "Holly Lane, Atherstone, CV9 2SQ",
            Latitude = 52.58607m,
            Longitude = -1.55878m
        };
        var stop = new LoadStop { Name = "Deliver · SUMMERBERRY · Aldi-Atherstone" };

        var matched = SamsaraStopSiteMatcher.FindSite(stop, [site]);

        Assert.Same(site, matched);
    }

    [Fact]
    public void Groups_adjacent_jobs_at_same_physical_site_but_preserves_collection_and_delivery_phases()
    {
        var delivery = Guid.NewGuid();
        var secondDelivery = Guid.NewGuid();
        var candidates = new[]
        {
            Candidate("NWF-Drayton", true, Guid.NewGuid()),
            Candidate("NWF-Merston", true, Guid.NewGuid()),
            Candidate("NWF-Runcton", true, Guid.NewGuid()),
            Candidate("NWF-Selsey", true, Guid.NewGuid()),
            Candidate("Aldi-Darlington", false, delivery, Guid.NewGuid()),
            Candidate("Aldi-Darlington", false, delivery, Guid.NewGuid()),
            Candidate("Morrisons-Stockton", false, secondDelivery, Guid.NewGuid()),
            Candidate("Morrisons-Stockton", false, secondDelivery, Guid.NewGuid()),
        };

        var groups = SamsaraPhysicalStopGrouping.GroupAdjacent(candidates);

        Assert.Equal(6, groups.Count);
        Assert.Equal(2, groups[4].Members.Count);
        Assert.Equal(2, groups[5].Members.Count);
        Assert.All(groups.Take(4), group => Assert.True(group.Representative.IsCollection));
        Assert.All(groups.Skip(4), group => Assert.False(group.Representative.IsCollection));
    }

    [Fact]
    public void Does_not_merge_a_return_visit_to_the_same_site()
    {
        var site = Guid.NewGuid();
        var groups = SamsaraPhysicalStopGrouping.GroupAdjacent(new[]
        {
            Candidate("Aldi", false, site),
            Candidate("Morrisons", false, Guid.NewGuid()),
            Candidate("Aldi", false, site),
        });

        Assert.Equal(3, groups.Count);
    }

    private static SamsaraPhysicalStopCandidate Candidate(string name, bool collection, Guid siteId, Guid? orderId = null) =>
        new(Guid.NewGuid(), name, siteId, $"{name} address", 50, -1, null, collection, orderId, null);
}
