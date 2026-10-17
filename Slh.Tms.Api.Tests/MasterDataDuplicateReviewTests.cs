using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MasterDataDuplicateReviewTests : IClassFixture<CustomWebFactory>
{
    private const string LyonsUser = "planner@lyonshaulage.com";
    private readonly CustomWebFactory _factory;

    public MasterDataDuplicateReviewTests(CustomWebFactory factory) => _factory = factory;

    [Fact]
    public void Preserve_never_blanks_existing_address_or_routing_text()
    {
        Assert.Equal("Oliver Kay Hoddesdon, EN11 0NX", MasterDataDuplicateReviewService.Preserve(" Oliver Kay Hoddesdon, EN11 0NX ", null));
        Assert.Equal("Leyland PR26 6TB", MasterDataDuplicateReviewService.Preserve("Leyland PR26 6TB", ""));
        Assert.Equal("Hall Hunter, Chichester", MasterDataDuplicateReviewService.Preserve(null, " Hall Hunter, Chichester "));
    }

    [Fact]
    public async Task Duplicate_site_review_surfaces_candidate_with_address_fields_to_preserve()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.AddRange(
                new Site { ExternalCode = $"OKH{suffix}A", Name = $"Oliver Kay Hoddesdon {suffix}", CollectionAddress = "Unit A, Bingley Road, Hoddesdon EN11 0NX", Active = true },
                new Site { ExternalCode = $"OKH{suffix}B", Name = $"Oliver Kay Hoddesdon {suffix}", CollectionAddress = "Bingley Road, Hoddesdon EN11 0NX", MapLink = "https://maps.example/okh", Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.GetAsync("/api/v1/operational-master-data/duplicates?entityType=sites");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidates = await response.Content.ReadFromJsonAsync<List<MasterDataDuplicateCandidate>>();
        var candidate = Assert.Single(candidates!.Where(x => x.Canonical.Name.Contains(suffix)));
        Assert.True(candidate.Confidence >= 95);
        Assert.True(candidate.CanAutoMerge);
        Assert.Contains("collectionAddress", candidate.PreservedFields);
        Assert.Contains("mapLink", candidate.PreservedFields);
    }

    [Fact]
    public async Task Merge_site_archives_duplicate_and_preserves_routing_address_details()
    {
        var canonicalId = Guid.NewGuid();
        var duplicateId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.AddRange(
                new Site { Id = canonicalId, ExternalCode = $"LEY{suffix}A", Name = $"Leyland {suffix}", CollectionAddress = null, Active = true },
                new Site { Id = duplicateId, ExternalCode = $"LEY{suffix}B", Name = $"Leyland {suffix}", CollectionAddress = "Leyland, Lancashire PR26 6TB", MapLink = "https://maps.example/leyland", Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.PostAsJsonAsync("/api/v1/operational-master-data/duplicates/sites/merge", new MasterDataDuplicateMergeRequest(canonicalId, [duplicateId], "test merge"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var canonical = await verifyDb.Sites.FindAsync(canonicalId);
        var duplicate = await verifyDb.Sites.FindAsync(duplicateId);
        Assert.Equal("Leyland, Lancashire PR26 6TB", canonical!.CollectionAddress);
        Assert.Equal("https://maps.example/leyland", canonical.MapLink);
        Assert.False(duplicate!.Active);
    }
}
