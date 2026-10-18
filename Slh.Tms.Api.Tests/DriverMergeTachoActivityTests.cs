using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverMergeTachoActivityTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public DriverMergeTachoActivityTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Driver_merge_with_two_members_keeps_the_one_still_driving()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
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
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
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
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
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
}

public sealed class DriverTachoActivityArchiveTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public DriverTachoActivityArchiveTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Drivers_without_tacho_activity_for_28_days_are_archived_and_come_back_when_driving()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var drivers = Enumerable.Range(1, 60).Select(i => new Driver { EmployeeNumber = $"A{i}", DisplayName = $"Active Driver {i}", TachoMasterDriverId = (500000 + i).ToString(), Active = true }).ToList();
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

        Assert.False(idle.Active);
        Assert.True(onRun.Active);
        Assert.All(drivers, driver => Assert.True(driver.Active));

        duties[600001] = now.AddHours(-5);
        var back = await DriverTachoActivity.ApplyAsync(db, activity with { LastDutyByMember = duties }, "test", now, CancellationToken.None);
        Assert.True(back.Restored >= 1);
        Assert.True(idle.Active);
    }

    [Fact]
    public async Task Driver_activity_check_does_nothing_when_the_tacho_feed_looks_thin()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var idle = new Driver { EmployeeNumber = "IDLE2", DisplayName = "Idle Two", TachoMasterDriverId = "600009", Active = true };
        db.Drivers.Add(idle);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var outcome = await DriverTachoActivity.ApplyAsync(db, new TachoRecentActivity(DateOnly.FromDateTime(now.AddDays(-28).UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime), now, new Dictionary<int, DateTimeOffset>(), []), "test", now, CancellationToken.None);
        Assert.Equal(0, outcome.Archived);
        Assert.True(idle.Active);
    }
}
