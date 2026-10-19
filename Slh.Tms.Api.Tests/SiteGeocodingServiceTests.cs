using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SiteGeocodingServiceTests
{
    [Fact]
    public async Task BackfillAsync_geocodes_sites_with_a_postcode_and_flags_those_without()
    {
        await using var db = NewDb();
        db.MasterSites.AddRange(
            new MasterSite { SiteId = "S1", SiteName = "Barfoots Sefter", Postcode = "PO21 3PX", IsActive = true },
            new MasterSite { SiteId = "S2", SiteName = "Aldi Darlington", Postcode = null, IsActive = true },
            new MasterSite { SiteId = "S3", SiteName = "Already Placed", Postcode = "PO22 0AQ", IsActive = true, Latitude = 1m, Longitude = 2m });
        await db.SaveChangesAsync();

        var client = ClientReturning("""
            {"status":200,"result":[{"query":"PO21 3PX","result":{"latitude":50.78,"longitude":-0.68}}]}
            """);
        var service = new SiteGeocodingService(db, client, NullLogger<SiteGeocodingService>.Instance);

        var result = await service.BackfillAsync(force: false, CancellationToken.None);

        Assert.Equal(1, result.Geocoded);
        Assert.Contains("Aldi Darlington", result.SitesWithoutPostcode);

        var sefter = await db.MasterSites.SingleAsync(s => s.SiteId == "S1");
        Assert.Equal(50.78m, sefter.Latitude);
        Assert.Equal(-0.68m, sefter.Longitude);

        // A site that already had coordinates is left untouched without force.
        var already = await db.MasterSites.SingleAsync(s => s.SiteId == "S3");
        Assert.Equal(1m, already.Latitude);
        Assert.Equal(1, result.AlreadyHadCoordinates);
    }

    [Fact]
    public async Task BackfillAsync_reports_postcodes_the_service_cannot_resolve()
    {
        await using var db = NewDb();
        db.MasterSites.Add(new MasterSite { SiteId = "S1", SiteName = "Mystery Depot", Postcode = "ZZ99 9ZZ", IsActive = true });
        await db.SaveChangesAsync();

        var client = ClientReturning("""{"status":200,"result":[{"query":"ZZ99 9ZZ","result":null}]}""");
        var service = new SiteGeocodingService(db, client, NullLogger<SiteGeocodingService>.Instance);

        var result = await service.BackfillAsync(force: false, CancellationToken.None);

        Assert.Equal(0, result.Geocoded);
        Assert.Contains(result.PostcodesNotFound, entry => entry.Contains("Mystery Depot"));
        var site = await db.MasterSites.SingleAsync(s => s.SiteId == "S1");
        Assert.Null(site.Latitude);
    }

    private static PostcodesIoClient ClientReturning(string bulkJson)
    {
        var http = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bulkJson, Encoding.UTF8, "application/json")
            }))
        { BaseAddress = new Uri("https://api.postcodes.io/") };
        return new PostcodesIoClient(http, NullLogger<PostcodesIoClient>.Instance);
    }

    private static TmsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase($"geocode-{Guid.NewGuid():N}").Options);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request, cancellationToken));
    }
}
