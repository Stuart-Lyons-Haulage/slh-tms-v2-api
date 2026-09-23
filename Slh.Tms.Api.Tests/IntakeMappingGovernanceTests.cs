using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class IntakeMappingGovernanceTests
{
    [Fact]
    public async Task Alias_normalisation_resolves_canonical_slh_site_and_region()
    {
        await using var db = CreateDb();
        var site = new Site { ExternalCode = "SLH-FRV", Name = "SLH-Lyons Consolidation Centre FRV", OperationalRegion = "South Coast" };
        db.Sites.Add(site);
        db.IntegrationMappings.Add(new IntegrationMapping
        {
            Provider = "InfoMailbox", ExternalKey = "slh distribution centre", ExternalLabel = "SLH Distribution Centre",
            TmsEntityType = "Site", TmsEntityId = site.Id, MappingKind = "SiteAlias", NormalizedExternalValue = "slhdistributioncentre", ConfidenceThreshold = 0.95m
        });
        await db.SaveChangesAsync();

        var match = await new IntakeMappingService(db).MatchSiteAsync("  SLH Distribution   Centre ", CancellationToken.None);

        Assert.True(match.Matched);
        Assert.Equal(site.Id, match.EntityId);
        Assert.Equal("SLH-Lyons Consolidation Centre FRV", match.CanonicalValue);
        Assert.Equal("South Coast", match.OperationalRegion);
        Assert.Equal("  SLH Distribution   Centre ", match.OriginalValue);
    }

    [Fact]
    public async Task Ambiguous_alias_requires_review_and_never_creates_master_data()
    {
        await using var db = CreateDb();
        var first = new Site { ExternalCode = "A", Name = "Farm A" };
        var second = new Site { ExternalCode = "B", Name = "Farm B" };
        db.Sites.AddRange(first, second);
        db.IntegrationMappings.AddRange(
            Mapping(first.Id, "north farm"), Mapping(second.Id, "north-farm"));
        await db.SaveChangesAsync();

        var before = await db.Sites.CountAsync();
        var match = await new IntakeMappingService(db).MatchSiteAsync("North Farm", CancellationToken.None);

        Assert.False(match.Matched);
        Assert.True(match.RequiresReview);
        Assert.Equal("ambiguous_site_alias", match.IssueCode);
        Assert.Equal(before, await db.Sites.CountAsync());
    }

    private static IntegrationMapping Mapping(Guid target, string value) => new()
    {
        Provider = "InfoMailbox", ExternalKey = value, TmsEntityType = "Site", TmsEntityId = target,
        MappingKind = "SiteAlias", NormalizedExternalValue = new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray())
    };

    private static TmsDbContext CreateDb() => new(new DbContextOptionsBuilder<TmsDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
