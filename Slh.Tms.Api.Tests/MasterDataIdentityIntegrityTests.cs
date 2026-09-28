using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MasterDataIdentityIntegrityTests
{
    [Fact]
    public async Task Driver_duplicate_merge_retires_old_member_owner_before_transferring_identity()
    {
        var setup = await CreateDbAsync();
        await using var connection = setup.Connection;
        await using var db = setup.Db;

        var canonical = new Driver
        {
            EmployeeNumber = "EMP-100",
            DisplayName = "Andrew Alfred Smith",
            Active = true
        };
        var duplicate = new Driver
        {
            EmployeeNumber = "TM-985459",
            DisplayName = "Andrew Alfred Smith",
            TachoMasterDriverId = "985459",
            Active = true
        };
        db.Drivers.AddRange(canonical, duplicate);
        await db.SaveChangesAsync();

        var result = await MasterDataDuplicateReviewService.MergeAsync(
            db,
            "drivers",
            new MasterDataDuplicateMergeRequest(canonical.Id, [duplicate.Id], "identity transfer regression"),
            "test",
            CancellationToken.None);

        Assert.Equal(1, result.Merged);
        Assert.Equal("985459", canonical.TachoMasterDriverId);
        Assert.True(canonical.Active);
        Assert.False(duplicate.Active);
    }

    [Fact]
    public async Task Driver_duplicate_merge_refuses_two_different_tachomaster_member_ids()
    {
        var setup = await CreateDbAsync();
        await using var connection = setup.Connection;
        await using var db = setup.Db;

        var canonical = new Driver
        {
            EmployeeNumber = "EMP-200",
            DisplayName = "Driver One",
            TachoMasterDriverId = "1540927",
            Active = true
        };
        var duplicate = new Driver
        {
            EmployeeNumber = "EMP-201",
            DisplayName = "Driver One",
            TachoMasterDriverId = "1163422",
            Active = true
        };
        db.Drivers.AddRange(canonical, duplicate);
        await db.SaveChangesAsync();

        var result = await MasterDataDuplicateReviewService.MergeAsync(
            db,
            "drivers",
            new MasterDataDuplicateMergeRequest(canonical.Id, [duplicate.Id], "conflicting identity regression"),
            "test",
            CancellationToken.None);

        Assert.Equal(0, result.Merged);
        Assert.Contains(result.Messages, message => message.Contains("more than one TachoMaster member identity", StringComparison.Ordinal));
        Assert.True(canonical.Active);
        Assert.True(duplicate.Active);
    }

    [Fact]
    public async Task Second_active_geofence_cannot_be_linked_to_the_same_site()
    {
        var setup = await CreateDbAsync();
        await using var connection = setup.Connection;
        await using var db = setup.Db;

        var site = new Site { ExternalCode = "SITE900", Name = "Integrity Site", Active = true };
        db.Sites.Add(site);
        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Integrity Fence A",
            NormalizedName = "INTEGRITY FENCE A",
            SiteId = site.Id,
            SiteNumber = site.ExternalCode,
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        });
        await db.SaveChangesAsync();

        db.SiteGeofences.Add(new SiteGeofence
        {
            Name = "Integrity Fence B",
            NormalizedName = "INTEGRITY FENCE B",
            SiteId = site.Id,
            SiteNumber = site.ExternalCode,
            PolygonJson = "[[2,2],[3,2],[2,3]]",
            Active = true
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        Assert.Contains("only have one active geofence", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Geofence_can_be_reassigned_when_previous_site_geofence_is_archived_in_same_save()
    {
        var setup = await CreateDbAsync();
        await using var connection = setup.Connection;
        await using var db = setup.Db;

        var site = new Site { ExternalCode = "SITE901", Name = "Reassignment Site", Active = true };
        var existing = new SiteGeofence
        {
            Name = "Old Fence",
            NormalizedName = "OLD FENCE",
            SiteId = site.Id,
            SiteNumber = site.ExternalCode,
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        };
        var replacement = new SiteGeofence
        {
            Name = "Replacement Fence",
            NormalizedName = "REPLACEMENT FENCE",
            PolygonJson = "[[2,2],[3,2],[2,3]]",
            Active = true
        };
        db.AddRange(site, existing, replacement);
        await db.SaveChangesAsync();

        existing.Active = false;
        replacement.SiteId = site.Id;
        replacement.SiteNumber = site.ExternalCode;
        await db.SaveChangesAsync();

        Assert.False(existing.Active);
        Assert.True(replacement.Active);
        Assert.Equal(site.Id, replacement.SiteId);
    }

    [Fact]
    public async Task Site_duplicate_merge_does_not_stack_geofences_on_canonical_site()
    {
        var setup = await CreateDbAsync();
        await using var connection = setup.Connection;
        await using var db = setup.Db;

        var canonical = new Site { ExternalCode = "SITE910", Name = "Duplicate Site", Active = true };
        var duplicate = new Site { ExternalCode = "SITE911", Name = "Duplicate Site", Active = true };
        var canonicalFence = new SiteGeofence
        {
            Name = "Canonical Fence",
            NormalizedName = "CANONICAL FENCE",
            SiteId = canonical.Id,
            SiteNumber = canonical.ExternalCode,
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        };
        var duplicateFence = new SiteGeofence
        {
            Name = "Duplicate Fence",
            NormalizedName = "DUPLICATE FENCE",
            SiteId = duplicate.Id,
            SiteNumber = duplicate.ExternalCode,
            PolygonJson = "[[2,2],[3,2],[2,3]]",
            Active = true
        };
        db.AddRange(canonical, duplicate, canonicalFence, duplicateFence);
        await db.SaveChangesAsync();

        var result = await MasterDataDuplicateReviewService.MergeAsync(
            db,
            "sites",
            new MasterDataDuplicateMergeRequest(canonical.Id, [duplicate.Id], "site geofence uniqueness regression"),
            "test",
            CancellationToken.None);

        Assert.Equal(1, result.Merged);
        Assert.False(duplicate.Active);
        Assert.Equal(canonical.Id, canonicalFence.SiteId);
        Assert.Null(duplicateFence.SiteId);
        Assert.Equal(1, await db.SiteGeofences.CountAsync(fence => fence.Active && fence.SiteId == canonical.Id));
        Assert.Contains(result.Messages, message => message.Contains("left 1 additional geofence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Automatic_geofence_sync_leaves_second_match_unlinked_for_review()
    {
        var setup = await CreateDbAsync();
        await using var connection = setup.Connection;
        await using var db = setup.Db;

        var site = new Site { ExternalCode = "NWF-SELSEY", CustomerCode = "NWF", Name = "NWF Selsey", Active = true };
        db.Sites.Add(site);
        db.SiteGeofences.AddRange(
            new SiteGeofence
            {
                Name = "NWF Selsey",
                NormalizedName = "NWF SELSEY",
                PolygonJson = "[[0,0],[1,0],[0,1]]",
                Active = true
            },
            new SiteGeofence
            {
                Name = "Selsey (Natures Way)",
                NormalizedName = "SELSEY (NATURES WAY)",
                PolygonJson = "[[2,2],[3,2],[2,3]]",
                Active = true
            });
        await db.SaveChangesAsync();

        var result = await SiteGeofenceMasterSync.SyncAsync(db, CancellationToken.None);

        var linked = await db.SiteGeofences.CountAsync(fence => fence.SiteId == site.Id);
        var unlinked = await db.SiteGeofences.CountAsync(fence => fence.SiteId == null);
        Assert.Equal(1, linked);
        Assert.Equal(1, unlinked);
        Assert.Contains(result.Warnings, warning => warning.Contains("already has an active geofence", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(SqliteConnection Connection, TmsDbContext Db)> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new TmsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return (connection, db);
    }
}
