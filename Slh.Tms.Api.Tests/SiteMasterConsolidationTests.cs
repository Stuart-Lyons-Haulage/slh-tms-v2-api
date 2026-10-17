using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SiteMasterConsolidationTests
{
    [Fact]
    public async Task Customer_location_is_promoted_to_site_and_matching_geofence_is_linked()
    {
        await using var db = CreateDb();
        db.Customers.Add(new Customer { Code = "ALDI-CDF", Name = "Aldi Cardiff" });
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Aldi Cardiff",
            NormalizedName = "ALDI CARDIFF",
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        });
        await db.SaveChangesAsync();

        var result = await SiteMasterConsolidation.ReconcileAsync(db, "test", CancellationToken.None);

        var site = Assert.Single(db.Sites.Where(x => x.ExternalCode == "ALDI-CDF"));
        Assert.Equal("Aldi Cardiff", site.Name);
        var fence = Assert.Single(db.SiteGeofences);
        Assert.Equal(site.Id, fence.SiteId);
        Assert.Equal("ALDI-CDF", fence.SiteNumber);
        Assert.Equal(1, result.PromotedCustomers);
        Assert.Equal(1, result.LinkedGeofences);
    }

    [Fact]
    public async Task Duplicate_unlinked_site_is_archived_when_same_name_has_the_geofence()
    {
        await using var db = CreateDb();
        var canonical = new Site { ExternalCode = "ALDI-CDF", Name = "Aldi Cardiff", Active = true };
        var duplicate = new Site { ExternalCode = "OLD-ALDI-CDF", Name = "ALDI CARDIFF", Active = true };
        db.Sites.AddRange(canonical, duplicate);
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Aldi Cardiff",
            NormalizedName = "ALDI CARDIFF",
            SiteId = canonical.Id,
            SiteNumber = canonical.ExternalCode,
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        });
        await db.SaveChangesAsync();

        var result = await SiteMasterConsolidation.ReconcileAsync(db, "test", CancellationToken.None);

        Assert.True(canonical.Active);
        Assert.False(duplicate.Active);
        Assert.Equal(1, result.ArchivedDuplicates);
    }

    [Fact]
    public async Task Alias_linked_nwf_duplicate_is_merged_into_the_geofence_backed_site_and_not_resurrected()
    {
        await using var db = CreateDb();
        var canonical = new Site { ExternalCode = "SITE363", Name = "Runcton", DriverTextName = "Runcton", Active = true };
        var duplicate = new Site { ExternalCode = "SITE330", Name = "NWF - Runcton", DriverTextName = "NWF Runcton", Active = true };
        db.Sites.AddRange(canonical, duplicate);
        db.Customers.Add(new Customer { Code = "SITE330", Name = "NWF - Runcton", Active = true });
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Runcton (Natures Way)",
            NormalizedName = "RUNCTON (NATURES WAY)",
            SiteId = canonical.Id,
            SiteNumber = canonical.ExternalCode,
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        });
        await db.SaveChangesAsync();
        await SaveAliasesAsync(db, canonical.ExternalCode, "NWF - Runcton; Natures Way Runcton");

        var result = await SiteMasterConsolidation.ReconcileAsync(db, "test", CancellationToken.None);

        Assert.True(canonical.Active);
        Assert.False(duplicate.Active);
        Assert.Equal(1, result.ArchivedDuplicates);
        Assert.Single(db.Sites.Where(x => x.Active && (x.Id == canonical.Id || x.Id == duplicate.Id)));

        var auditProcessor = new AuditOutboxProcessor(db, NullLogger<AuditOutboxProcessor>.Instance);
        await auditProcessor.ProcessPendingAsync(CancellationToken.None);
        var mergeAudit = Assert.Single(db.MasterDataAudits.Where(x => x.EntityId == duplicate.Id && x.Action == "MergedDuplicate"));
        using (var audit = JsonDocument.Parse(mergeAudit.ChangesJson!))
            Assert.Equal(canonical.Id, audit.RootElement.GetProperty("canonicalSiteId").GetGuid());

        var canonicalReloaded = Assert.Single(await db.Sites.Where(x => x.Id == canonical.Id).ToListAsync());
        await MasterDetailStore.EnrichSitesAsync(db, new[] { canonicalReloaded }, CancellationToken.None);
        Assert.Contains("NWF - Runcton", canonicalReloaded.Aliases ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SITE330", canonicalReloaded.Aliases ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Operator_archived_site_is_not_reactivated_by_legacy_customer_sync()
    {
        await using var db = CreateDb();
        var canonical = new Site { ExternalCode = "SITE363", Name = "Runcton", DriverTextName = "Runcton", Active = true };
        var archived = new Site { ExternalCode = "SITE330", Name = "NWF - Runcton", DriverTextName = "NWF Runcton", Active = false };
        db.Sites.AddRange(canonical, archived);
        db.Customers.Add(new Customer { Code = "SITE330", Name = "NWF - Runcton", Active = true });
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Site",
            EntityId = archived.Id,
            Action = "Archived",
            ChangedBy = "planner@test",
            ChangesJson = "{}"
        });
        await db.SaveChangesAsync();
        await SaveAliasesAsync(db, canonical.ExternalCode, "NWF - Runcton; Natures Way Runcton");

        var result = await SiteMasterConsolidation.ReconcileAsync(db, "test", CancellationToken.None);

        Assert.True(canonical.Active);
        Assert.False(archived.Active);
        Assert.Equal(0, result.PromotedCustomers);
        Assert.Equal(0, result.NeedsReview);
        Assert.Single(db.Sites.Where(x => x.Active && (x.Id == canonical.Id || x.Id == archived.Id)));
    }

    [Fact]
    public async Task Ambiguous_duplicate_sites_are_flagged_instead_of_guessed()
    {
        await using var db = CreateDb();
        db.Sites.AddRange(
            new Site { ExternalCode = "A1", Name = "Aldi Cardiff", CollectionAddress = "1 North Road, Cardiff, CF10 1AA", Active = true },
            new Site { ExternalCode = "A2", Name = "ALDI CARDIFF", CollectionAddress = "2 South Road, Cardiff, CF11 2BB", Active = true });
        await db.SaveChangesAsync();

        var result = await SiteMasterConsolidation.ReconcileAsync(db, "test", CancellationToken.None);

        Assert.Equal(0, result.ArchivedDuplicates);
        Assert.Equal(1, result.NeedsReview);
        var review = Assert.Single(db.StagedImports.Where(x => x.EntityType == "masterdata:site-review"));
        Assert.Equal(StagingStatus.PendingReview, review.Status);
    }

    [Fact]
    public async Task Unlinked_geofence_without_unique_site_match_is_flagged()
    {
        await using var db = CreateDb();
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Aldi Unknown Depot",
            NormalizedName = "ALDI UNKNOWN DEPOT",
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        });
        await db.SaveChangesAsync();

        var result = await SiteMasterConsolidation.ReconcileAsync(db, "test", CancellationToken.None);

        Assert.Equal(0, result.LinkedGeofences);
        Assert.Equal(1, result.NeedsReview);
        var review = Assert.Single(db.StagedImports.Where(x => x.EntityType == "masterdata:geofence-review"));
        Assert.Equal(StagingStatus.PendingReview, review.Status);
    }

    [Fact]
    public async Task Matching_archived_site_code_is_restored_instead_of_duplicated()
    {
        await using var db = CreateDb();
        var archived = new Site { ExternalCode = "ALDI-CDF", Name = "Aldi Cardiff", Active = false };
        db.Sites.Add(archived);
        db.Customers.Add(new Customer { Code = "ALDI-CDF", Name = "Aldi Cardiff" });
        await db.SaveChangesAsync();

        await SiteMasterConsolidation.ReconcileAsync(db, "test", CancellationToken.None);

        var site = Assert.Single(db.Sites.Where(x => x.ExternalCode == "ALDI-CDF"));
        Assert.True(site.Active);
        Assert.Equal(archived.Id, site.Id);
    }

    private static Task SaveAliasesAsync(TmsDbContext db, string siteCode, string aliases)
        => MasterDetailStore.SaveAsync(
            db,
            "site",
            siteCode,
            JsonSerializer.Serialize(new { externalCode = siteCode, aliases }),
            "test",
            "test",
            CancellationToken.None);

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"site-master-consolidation-{Guid.NewGuid()}")
            .Options;
        return new TmsDbContext(options);
    }
}
