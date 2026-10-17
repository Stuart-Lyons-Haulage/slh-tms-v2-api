using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class GeofenceProviderSitePlaceholderTests
{
    [Fact]
    public void Embedded_payload_does_not_expose_dot_site_one_as_slh_site_one()
    {
        var selsey = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(fence => fence.Name == "Selsey (Natures Way)"));
        Assert.Null(selsey.SiteNumber);

        var explicitProviderCodes = EmbeddedGeofenceEngine.ApprovedFences
            .Where(fence => !string.IsNullOrWhiteSpace(fence.SiteNumber))
            .Select(fence => fence.SiteNumber)
            .Distinct()
            .ToList();
        Assert.DoesNotContain("1", explicitProviderCodes);
        Assert.Contains("3", explicitProviderCodes);
    }

    [Fact]
    public async Task Repair_clears_false_site_one_link_without_touching_polygon_or_visit_history()
    {
        await using var db = Database();
        var wrongSite = new Site { Id = Guid.NewGuid(), ExternalCode = "1", Name = "Winchester Southbound", Active = true };
        var fence = new SiteGeofence
        {
            Id = Guid.NewGuid(),
            Name = "Selsey (Natures Way)",
            NormalizedName = NormalizeName("Selsey (Natures Way)"),
            SiteNumber = "1",
            SiteId = wrongSite.Id,
            PolygonJson = "[[1,2],[2,3],[3,1]]",
            Active = true
        };
        var visit = new GeofenceVisit
        {
            Id = Guid.NewGuid(),
            GeofenceId = fence.Id,
            VehicleIdentifier = "SLH225",
            EnteredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            ExitedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastInsideAtUtc = DateTimeOffset.UtcNow.AddMinutes(-11),
            DwellMinutes = 20,
            Status = "Departed"
        };
        db.Sites.Add(wrongSite);
        db.SiteGeofences.Add(fence);
        db.GeofenceVisits.Add(visit);
        await db.SaveChangesAsync();

        var result = await GeofenceProviderPlaceholderRepair.EnsureAsync(db, CancellationToken.None);

        Assert.Equal(1, result.Found);
        Assert.Equal(0, result.Relinked);
        Assert.Equal(1, result.Cleared);
        var repaired = await db.SiteGeofences.SingleAsync();
        Assert.Null(repaired.SiteNumber);
        Assert.Null(repaired.SiteId);
        Assert.Equal("[[1,2],[2,3],[3,1]]", repaired.PolygonJson);
        Assert.Equal(fence.Id, (await db.GeofenceVisits.SingleAsync()).GeofenceId);
    }

    [Fact]
    public async Task Repair_keeps_true_site_one_only_when_geofence_name_matches_site_master()
    {
        await using var db = Database();
        var winchester = new Site { Id = Guid.NewGuid(), ExternalCode = "1", Name = "Winchester Southbound", Active = true };
        var fence = new SiteGeofence
        {
            Id = Guid.NewGuid(),
            Name = "Winchester Southbound",
            NormalizedName = NormalizeName("Winchester Southbound"),
            SiteNumber = "1",
            SiteId = winchester.Id,
            PolygonJson = "[]",
            Active = true
        };
        db.Sites.Add(winchester);
        db.SiteGeofences.Add(fence);
        await db.SaveChangesAsync();

        var result = await GeofenceProviderPlaceholderRepair.EnsureAsync(db, CancellationToken.None);

        Assert.Equal(1, result.Found);
        Assert.Equal(0, result.Cleared);
        var repaired = await db.SiteGeofences.SingleAsync();
        Assert.Equal("1", repaired.SiteNumber);
        Assert.Equal(winchester.Id, repaired.SiteId);
    }

    [Fact]
    public async Task Repair_relinks_placeholder_by_exact_site_name_to_that_sites_canonical_code()
    {
        await using var db = Database();
        var winchester = new Site { Id = Guid.NewGuid(), ExternalCode = "1", Name = "Winchester Southbound", Active = true };
        var selsey = new Site { Id = Guid.NewGuid(), ExternalCode = "NWF-SELSEY", Name = "Selsey (Natures Way)", Active = true };
        var fence = new SiteGeofence
        {
            Id = Guid.NewGuid(),
            Name = "Selsey (Natures Way)",
            NormalizedName = NormalizeName("Selsey (Natures Way)"),
            SiteNumber = "1",
            SiteId = winchester.Id,
            PolygonJson = "[]",
            Active = true
        };
        db.Sites.AddRange(winchester, selsey);
        db.SiteGeofences.Add(fence);
        await db.SaveChangesAsync();

        var result = await GeofenceProviderPlaceholderRepair.EnsureAsync(db, CancellationToken.None);

        Assert.Equal(1, result.Relinked);
        var repaired = await db.SiteGeofences.SingleAsync();
        Assert.Equal("NWF-SELSEY", repaired.SiteNumber);
        Assert.Equal(selsey.Id, repaired.SiteId);
    }

    [Fact]
    public async Task Location_only_and_explicit_non_placeholder_manual_links_are_not_rewritten()
    {
        await using var db = Database();
        var site = new Site { Id = Guid.NewGuid(), ExternalCode = "SITE-0023", Name = "Barfoots Sefter", Active = true };
        db.Sites.Add(site);
        db.SiteGeofences.AddRange(
            new SiteGeofence
            {
                Name = "Selsey Despatch",
                NormalizedName = NormalizeName("Selsey Despatch"),
                SiteNumber = "SITE-0023",
                SiteId = site.Id,
                PolygonJson = "[]",
                Active = true
            },
            new SiteGeofence
            {
                Name = "Landmark only",
                NormalizedName = NormalizeName("Landmark only"),
                SiteNumber = "LOCATION_ONLY",
                PolygonJson = "[]",
                Active = true
            });
        await db.SaveChangesAsync();

        var result = await GeofenceProviderPlaceholderRepair.EnsureAsync(db, CancellationToken.None);

        Assert.Equal(0, result.Found);
        var rows = await db.SiteGeofences.OrderBy(fence => fence.Name).ToListAsync();
        Assert.Contains(rows, fence => fence.SiteNumber == "SITE-0023" && fence.SiteId == site.Id);
        Assert.Contains(rows, fence => fence.SiteNumber == "LOCATION_ONLY" && fence.SiteId == null);
    }

    private static TmsDbContext Database()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TmsDbContext(options);
    }

    private static string NormalizeName(string value) =>
        string.Join(' ', value.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
