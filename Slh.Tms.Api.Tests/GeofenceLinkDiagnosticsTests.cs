using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class GeofenceLinkDiagnosticsTests
{
    [Fact]
    public void Numeric_site_codes_match_despite_leading_zeroes()
    {
        var site = Site("00042", "Example Foods Portsmouth");
        var fence = Fence("Example Foods Portsmouth", "42");

        var result = GeofenceLinkDiagnostics.Analyze(fence, new[] { site });

        Assert.True(result.SafeToAutoLink);
        Assert.Equal("ExactCode", result.Reason);
        Assert.Equal(site.Id, result.SuggestedSiteId);
    }

    [Fact]
    public void Exact_alias_match_is_safe_when_alias_resolves_to_one_site()
    {
        var site = Site("10", "Nature Foods Chichester", "Natures Way Foods Selsey");
        var fence = Fence("Natures Way Foods Selsey", null);

        var result = GeofenceLinkDiagnostics.Analyze(fence, new[] { site });

        Assert.True(result.SafeToAutoLink);
        Assert.Equal("ExactNameOrAlias", result.Reason);
        Assert.Equal(site.Id, result.SuggestedSiteId);
    }

    [Fact]
    public void Shared_customer_name_is_reported_as_ambiguous_not_auto_linked()
    {
        var first = Site("1", "Morrisons Wakefield");
        var second = Site("2", "Morrisons Sittingbourne");
        var fence = Fence("Morrisons RDC", null);

        var result = GeofenceLinkDiagnostics.Analyze(fence, new[] { first, second });

        Assert.False(result.SafeToAutoLink);
        Assert.StartsWith("Ambiguous", result.Reason);
        Assert.Null(result.SuggestedSiteId);
    }

    [Fact]
    public void Duplicate_alias_rows_for_same_site_do_not_create_false_ambiguity()
    {
        var id = Guid.NewGuid();
        var canonical = Site("9", "Vitacress Runcton", id: id);
        var alias = Site("9", "Vitacress Runcton", "Natures Way Foods Runcton", id);
        var fence = Fence("Natures Way Foods Runcton", null);

        var result = GeofenceLinkDiagnostics.Analyze(fence, new[] { canonical, alias });

        Assert.True(result.SafeToAutoLink);
        Assert.Equal(id, result.SuggestedSiteId);
    }

    private static Site Site(string code, string name, string? driverTextName = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ExternalCode = code,
        Name = name,
        DriverTextName = driverTextName,
        Active = true
    };

    private static EmbeddedFence Fence(string name, string? siteNumber) => new(
        Guid.NewGuid(),
        name,
        "Customer",
        null,
        null,
        0,
        0,
        siteNumber,
        new[] { new GeoPoint(0, 0), new GeoPoint(1, 0), new GeoPoint(1, 1) });
}
