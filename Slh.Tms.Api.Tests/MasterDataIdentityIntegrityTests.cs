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
        Assert.Contains(result.Messages, message => message.Contains("different TachoMaster members", StringComparison.Ordinal));
        Assert.True(canonical.Active);
        Assert.True(duplicate.Active);
    }

    [Fact]
    public async Task Driver_merge_with_two_members_keeps_the_one_still_driving()
    {
        var setup = await CreateDbAsync();
        await using var connection = setup.Connection;
        await using var db = setup.Db;
        var keep = new Driver { EmployeeNumber = "726", DisplayName = "Simon Vaughan Baker", TachoMasterDriverId = "1918226", TachoCardNumber = "DB213594516504", Active = true };
        var old = new Driver { EmployeeNumber = "TM-1920057", DisplayName = "Baker, Simon Vaughan", TachoMasterDriverId = "1920057", TachoCardNumber = "DB242269792894", Active = true };
        db.Drivers.AddRange(keep, old);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var activity = new TachoRecentActivity(DateOnly.FromDateTime(now.AddDays(-28).UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime), now,
            new Dictionary<int, DateTimeOffset> { [1918226] = now.AddDays(-1) }, []);
        var result = await MasterDataDuplicateReviewService.MergeAsync(db, "drivers", new MasterDataDuplicateMergeRequest(keep.Id, [old.Id], "two members"), "planner@lyonshaulage.com", CancellationToken.None, activity);

        Assert.Equal(1, result.Merged);
        Assert.Equal("1918226", keep.TachoMasterDriverId);
        Assert.True(keep.Active);
        Assert.False(old.Active);
        Assert.Contains(old.Id, await DriverTachoActivity.HeldArchivedAsync(db, CancellationToken.None));
    }

    [Fact]
    public async Task Driver_merge_takes_the_driving_member_from_the_row_merged_in()
    {
        var setup = await CreateDbAsync();
        await using var connection = setup.Connection;
        await using var db = setup.Db;
        var keep = new Driver { EmployeeNumber = "593", DisplayName = "Mateusz Lechowicz", TachoMasterDriverId = "1955729", Active = true };
        var current = new Driver { EmployeeNumber = "TM-1029109", DisplayName = "Lechowicz, Mateusz", TachoMasterDriverId = "1029109", TachoCardNumber = "DB171291620337", Active = true };
        db.Drivers.AddRange(keep, current);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var activity = new TachoRecentActivity(DateOnly.FromDateTime(now.AddDays(-28).UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime), now,
            new Dictionary<int, DateTimeOffset> { [1029109] = now.AddDays(-2) }, []);
        var result = await MasterDataDuplicateReviewService.MergeAsync(db, "drivers", new MasterDataDuplicateMergeRequest(keep.Id, [current.Id], "carry identity"), "planner@lyonshaulage.com", CancellationToken.None, activity);

        Assert.Equal(1, result.Merged);
        Assert.Equal("1029109", keep.TachoMasterDriverId);
        Assert.Equal("DB171291620337", keep.TachoCardNumber);
    }

    [Fact]
    public async Task Driver_merge_is_refused_when_both_members_drove_recently()
    {
        var setup = await CreateDbAsync();
        await using var connection = setup.Connection;
        await using var db = setup.Db;
        var a = new Driver { EmployeeNumber = "E-1", DisplayName = "Same Name", TachoMasterDriverId = "111111", Active = true };
        var b = new Driver { EmployeeNumber = "E-2", DisplayName = "Name, Same", TachoMasterDriverId = "222222", Active = true };
        db.Drivers.AddRange(a, b);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var activity = new TachoRecentActivity(DateOnly.FromDateTime(now.AddDays(-28).UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime), now,
            new Dictionary<int, DateTimeOffset> { [111111] = now.AddDays(-1), [222222] = now.AddDays(-3) }, []);
        var result = await MasterDataDuplicateReviewService.MergeAsync(db, "drivers", new MasterDataDuplicateMergeRequest(a.Id, [b.Id], "two people"), "planner@lyonshaulage.com", CancellationToken.None, activity);

        Assert.Equal(0, result.Merged);
        Assert.Contains(result.Messages, message => message.Contains("both driven", StringComparison.Ordinal));
        Assert.True(b.Active);
    }

    [Fact]
    public async Task Drivers_without_tacho_activity_for_28_days_are_archived_and_come_back_when_driving()
    {
        var setup = await CreateDbAsync();
        await using var connection = setup.Connection;
        await using var db = setup.Db;
        var now = DateTimeOffset.UtcNow;
        var drivers = Enumerable.Range(1, 20).Select(i => new Driver { EmployeeNumber = $"A{i}", DisplayName = $"Active Driver {i}", TachoMasterDriverId = (500000 + i).ToString(), Active = true }).ToList();
        var idle = new Driver { EmployeeNumber = "IDLE", DisplayName = "Idle Driver", TachoMasterDriverId = "600001", Active = true };
        var onRun = new Driver { EmployeeNumber = "RUN", DisplayName = "Run Driver", TachoMasterDriverId = "600002", Active = true };
        db.Drivers.AddRange(drivers);
        db.Drivers.AddRange(idle, onRun);
        db.Loads.Add(new Load { Reference = "RUN-1", PlanningDate = DateOnly.FromDateTime(now.UtcDateTime), DriverId = onRun.Id });
        await db.SaveChangesAsync();

        var duties = drivers.ToDictionary(driver => int.Parse(driver.TachoMasterDriverId!), _ => now.AddDays(-1));
        duties[600001] = now.AddDays(-40);
        var activity = new TachoRecentActivity(DateOnly.FromDateTime(now.AddDays(-28).UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime), now, duties, []);
        var outcome = await DriverTachoActivity.ApplyAsync(db, activity, "test", now, CancellationToken.None);

        Assert.Equal(1, outcome.Archived);
        Assert.False(idle.Active);
        Assert.True(onRun.Active);
        Assert.All(drivers, driver => Assert.True(driver.Active));

        duties[600001] = now.AddHours(-5);
        var back = await DriverTachoActivity.ApplyAsync(db, activity with { LastDutyByMember = duties }, "test", now, CancellationToken.None);
        Assert.Equal(1, back.Restored);
        Assert.True(idle.Active);
    }

    [Fact]
    public async Task Driver_activity_check_does_nothing_when_the_tacho_feed_looks_thin()
    {
        var setup = await CreateDbAsync();
        await using var connection = setup.Connection;
        await using var db = setup.Db;
        var idle = new Driver { EmployeeNumber = "IDLE2", DisplayName = "Idle Two", TachoMasterDriverId = "600009", Active = true };
        db.Drivers.Add(idle);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var outcome = await DriverTachoActivity.ApplyAsync(db, new TachoRecentActivity(DateOnly.FromDateTime(now.AddDays(-28).UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime), now, new Dictionary<int, DateTimeOffset>(), []), "test", now, CancellationToken.None);
        Assert.Equal(0, outcome.Archived);
        Assert.True(idle.Active);
    }

    [Fact]
    public async Task Multiple_active_geofences_can_be_linked_to_the_same_site()
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

        await db.SaveChangesAsync();

        var linked = await db.SiteGeofences
            .Where(fence => fence.SiteId == site.Id && fence.Active)
            .CountAsync();
        Assert.Equal(2, linked);
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
