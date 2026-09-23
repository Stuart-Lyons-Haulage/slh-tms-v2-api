using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MasterDetailStoreDriverEnrichmentTests
{
    [Fact]
    public async Task Driver_enrichment_does_not_throw_when_active_employee_numbers_are_duplicated()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"driver-enrichment-{Guid.NewGuid():N}")
            .Options;

        await using var db = new TmsDbContext(options);
        db.Drivers.AddRange(
            new Driver { EmployeeNumber = "DUP-01", DisplayName = "Canonical Driver" },
            new Driver { EmployeeNumber = "dup 01", DisplayName = "Duplicate Driver" });
        db.StagedImports.Add(new StagedImport
        {
            EntityType = "masterdetail:driver",
            IdempotencyKey = "driver:dup-01",
            PayloadJson = "{\"employeeNumber\":\"DUP-01\",\"tachoMasterDriverId\":\"298\"}",
            Status = StagingStatus.Promoted
        });
        await db.SaveChangesAsync();

        var drivers = await db.Drivers.AsNoTracking().ToListAsync();

        var exception = await Record.ExceptionAsync(
            () => MasterDetailStore.EnrichDriversAsync(db, drivers, CancellationToken.None));

        Assert.Null(exception);
        Assert.Contains(drivers, driver => driver.TachoMasterDriverId == "298");
    }
}
