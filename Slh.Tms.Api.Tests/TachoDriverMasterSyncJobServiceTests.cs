using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoDriverMasterSyncJobServiceTests
{
    [Fact]
    public async Task Startup_recovery_releases_legacy_running_job_without_a_worker_lease()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = CreateDb();
        var stranded = RunningRow(
            now.AddMinutes(-6),
            workerInstanceId: null,
            heartbeatUtc: null);
        db.StagedImports.Add(stranded);
        await db.SaveChangesAsync();

        var service = new TachoDriverMasterSyncJobService(db);
        var recoveredCount = await RecoverAsync(service, "worker:new-revision");

        var recovered = await db.StagedImports.SingleAsync(row => row.Id == stranded.Id);
        Assert.Equal(1, recoveredCount);
        Assert.Equal(StagingStatus.Failed, recovered.Status);

        var replacement = await service.EnqueueAsync("system:new-revision", CancellationToken.None);
        Assert.Equal("queued", replacement.Status);
        Assert.NotEqual(stranded.Id, replacement.JobId);
    }

    [Fact]
    public async Task Fresh_worker_lease_is_not_recovered_by_another_replica()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = CreateDb();
        var running = RunningRow(
            now.AddMinutes(-10),
            workerInstanceId: "worker:live",
            heartbeatUtc: now.AddSeconds(-20));
        db.StagedImports.Add(running);
        await db.SaveChangesAsync();

        var service = new TachoDriverMasterSyncJobService(db);
        var recoveredCount = await RecoverAsync(service, "worker:other");

        var unchanged = await db.StagedImports.SingleAsync(row => row.Id == running.Id);
        Assert.Equal(0, recoveredCount);
        Assert.Equal(StagingStatus.Approved, unchanged.Status);
    }

    [Fact]
    public async Task Expired_worker_lease_is_recovered_even_when_the_same_instance_returns_to_polling()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = CreateDb();
        var running = RunningRow(
            now.AddMinutes(-10),
            workerInstanceId: "worker:dead",
            heartbeatUtc: now.AddMinutes(-3));
        db.StagedImports.Add(running);
        await db.SaveChangesAsync();

        var service = new TachoDriverMasterSyncJobService(db);
        var recoveredCount = await RecoverAsync(service, "worker:dead");

        var recovered = await db.StagedImports.SingleAsync(row => row.Id == running.Id);
        Assert.Equal(1, recoveredCount);
        Assert.Equal(StagingStatus.Failed, recovered.Status);
        Assert.Contains("lease", recovered.ReviewNote ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Completed_queue_slot_is_reused_instead_of_creating_another_job_row()
    {
        await using var db = CreateDb();
        var service = new TachoDriverMasterSyncJobService(db);

        var first = await service.EnqueueAsync("system:first", CancellationToken.None);
        var slot = await db.StagedImports.SingleAsync(row => row.Id == first.JobId);
        slot.Status = StagingStatus.Failed;
        slot.ReviewedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var second = await service.EnqueueAsync("system:second", CancellationToken.None);

        Assert.Equal(first.JobId, second.JobId);
        Assert.Equal("queued", second.Status);
        Assert.Equal(1, await db.StagedImports.CountAsync(row => row.EntityType == "tachodrivermastersyncjob"));
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TmsDbContext(options);
    }

    private static StagedImport RunningRow(
        DateTimeOffset startedAtUtc,
        string? workerInstanceId,
        DateTimeOffset? heartbeatUtc)
    {
        var actor = "system:previous-revision";
        return new StagedImport
        {
            EntityType = "tachodrivermastersyncjob",
            IdempotencyKey = $"tachodrivermastersyncjob:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(new
            {
                actor,
                requestedAtUtc = startedAtUtc.AddSeconds(-1),
                startedAtUtc,
                message = "Canonical TachoMaster Driver Master sync is running.",
                workerInstanceId,
                heartbeatUtc
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Source = "System canonical Driver Master queue",
            Status = StagingStatus.Approved,
            ReceivedAtUtc = startedAtUtc,
            ReviewedAtUtc = startedAtUtc,
            ReviewedBy = actor,
            ReviewNote = "Canonical TachoMaster Driver Master sync is running."
        };
    }

    private static async Task<int> RecoverAsync(TachoDriverMasterSyncJobService service, string currentWorker)
    {
        var recover = typeof(TachoDriverMasterSyncJobService).GetMethod(
            "RecoverInterruptedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(recover);

        var task = Assert.IsType<Task<int>>(recover!.Invoke(service, [currentWorker, CancellationToken.None]));
        return await task;
    }
}
