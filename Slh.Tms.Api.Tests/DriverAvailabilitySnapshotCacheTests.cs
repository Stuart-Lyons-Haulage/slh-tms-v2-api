using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverAvailabilitySnapshotCacheTests
{
    private static int dateOffset;

    [Fact]
    public async Task StaffingAndDispatchReadersShareSnapshotUntilStaffingInvalidatesIt()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"availability-cache-{Guid.NewGuid():N}")
            .Options;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var db = new TmsDbContext(options);
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(Interlocked.Increment(ref dateOffset));
        db.Drivers.Add(new Driver { EmployeeNumber = "701", DisplayName = "First Driver", DriverType = "Employed", DriverGroup = "Employed", Active = true });
        await db.SaveChangesAsync();

        var staffingSnapshot = await DriverAvailabilityService.ReadAsync(db, date, CancellationToken.None,
            logger: NullLogger.Instance, cache: cache);
        db.Drivers.Add(new Driver { EmployeeNumber = "702", DisplayName = "Second Driver", DriverType = "Employed", DriverGroup = "Employed", Active = true });
        await db.SaveChangesAsync();

        var dispatchSnapshot = await DriverAvailabilityService.ReadAsync(db, date, CancellationToken.None,
            logger: NullLogger.Instance, cache: cache);
        Assert.Equal(staffingSnapshot.GeneratedAtUtc, dispatchSnapshot.GeneratedAtUtc);
        Assert.Single(dispatchSnapshot.Drivers);

        DriverAvailabilityService.Invalidate(cache);
        var refreshedSnapshot = await DriverAvailabilityService.ReadAsync(db, date, CancellationToken.None,
            logger: NullLogger.Instance, cache: cache);
        Assert.Equal(2, refreshedSnapshot.Drivers.Count);
    }
}
