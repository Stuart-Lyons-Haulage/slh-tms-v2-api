using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverTachoIdentityPersistenceTests
{
    [Fact]
    public async Task Tacho_identity_survives_a_driver_reload_for_wallboard_correlation()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"driver-tacho-{Guid.NewGuid():N}")
            .Options;

        await using (var writeDb = new TmsDbContext(options))
        {
            writeDb.Drivers.Add(new Driver
            {
                EmployeeNumber = "SLH001",
                DisplayName = "Test Driver",
                TachoMasterDriverId = "298",
                LastTachoSyncUtc = DateTimeOffset.UtcNow
            });

            await writeDb.SaveChangesAsync();
        }

        await using (var readDb = new TmsDbContext(options))
        {
            var driver = await readDb.Drivers.SingleAsync();

            Assert.Equal("298", driver.TachoMasterDriverId);
            Assert.NotNull(driver.LastTachoSyncUtc);
        }
    }

    [Fact]
    public void Tacho_master_identity_has_a_non_unique_lookup_index()
    {
        using var db = new TmsDbContext(
            new DbContextOptionsBuilder<TmsDbContext>()
                .UseInMemoryDatabase($"driver-tacho-model-{Guid.NewGuid():N}")
                .Options);

        var index = db.Model.FindEntityType(typeof(Driver))!
            .GetIndexes()
            .Single(candidate => candidate.Properties.Any(property => property.Name == nameof(Driver.TachoMasterDriverId)));

        Assert.Equal("IX_Drivers_TachoMasterDriverId", index.GetDatabaseName());
        Assert.False(index.IsUnique);
    }
}
