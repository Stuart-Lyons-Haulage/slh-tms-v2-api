using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SiteMasterIdentityResolverTests
{
    [Fact]
    public void Resolve_matches_existing_site_by_external_code()
    {
        var site = new Site { ExternalCode = "SITE0010", Name = "Aldi Bolton", CollectionAddress = "Bolton BL5 1EE", Active = true };

        var result = SiteMasterIdentityResolver.Resolve(new IncomingSiteIdentity("SITE0010", "Different Name", null, null, null, null), [site]);

        Assert.True(result.Matched);
        Assert.Equal(site.Id, result.Site!.Id);
        Assert.Equal(100, result.Confidence);
    }

    [Fact]
    public void Resolve_matches_existing_site_by_name_and_postcode()
    {
        var site = new Site { ExternalCode = "SITE0020", Name = "Aldi Goldthorpe", CollectionAddress = "Goldthorpe S63 9BL", Active = true };

        var result = SiteMasterIdentityResolver.Resolve(new IncomingSiteIdentity(null, "ALDI Goldthorpe", null, "Somewhere else S63 9BL", null, null), [site]);

        Assert.True(result.Matched);
        Assert.Equal(site.Id, result.Site!.Id);
        Assert.Equal("Matched by site name and postcode.", result.Reason);
    }

    [Fact]
    public void Resolve_matches_existing_site_by_alias()
    {
        var site = new Site { ExternalCode = "SITE0030", Name = "Hall Hunter Partnership", CollectionAddress = "Chichester PO20 1AA", Aliases = "HHP, Hall Hunter", Active = true };

        var result = SiteMasterIdentityResolver.Resolve(new IncomingSiteIdentity(null, "HHP", null, "PO20 1AA", null, null), [site]);

        Assert.True(result.Matched);
        Assert.Equal(site.Id, result.Site!.Id);
    }

    [Fact]
    public void Resolve_holds_weak_unmatched_site_for_review()
    {
        var result = SiteMasterIdentityResolver.Resolve(new IncomingSiteIdentity(null, "Aldi", null, null, null, null), []);

        Assert.False(result.Matched);
        Assert.False(result.CanCreate);
        Assert.True(result.RequiresReview);
        Assert.Equal("weak", result.Outcome);
    }

    [Fact]
    public void Resolve_allows_new_site_when_name_and_postcode_present()
    {
        var result = SiteMasterIdentityResolver.Resolve(new IncomingSiteIdentity(null, "New Distribution Centre", null, "Unit 1, Test Road, AB12 3CD", null, null), []);

        Assert.False(result.Matched);
        Assert.True(result.CanCreate);
        Assert.False(result.RequiresReview);
        Assert.Equal("new", result.Outcome);
    }
}
