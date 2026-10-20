using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationalOrderMaintenanceTests
{
    [Theory]
    [InlineData("Holly Lane, Atherstone, CV9 2SQ")]
    [InlineData(null)]
    public async Task Register_address_amendment_round_trips_through_operational_projection(string? address)
    {
        await using var db = new TmsDbContext(new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var date = new DateOnly(2026, 10, 21);
        var row = new StagedImport
        {
            EntityType = "register:order", IdempotencyKey = Guid.NewGuid().ToString(),
            Source = "test", Status = StagingStatus.Promoted,
            PayloadJson = JsonSerializer.Serialize(new
            {
                reference = "SB-676", poNumber = "SB-676", customerCode = "SUMMERBERRY",
                collectionDate = "2026-10-21",
                driverInstructions = "Delivery address: Old address · Customer ref: Old ref"
            })
        };
        db.StagedImports.Add(row);
        await db.SaveChangesAsync();
        var controller = new OperationalOrderMaintenanceController(db,
            NullLogger<OperationalOrderMaintenanceController>.Instance, new SiteAddressPropagationService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var request = new OperationalOrderUpdateRequest("SB-676", "SUMMERBERRY", date, date,
            null, null, 5, "Colworth Farm", "Aldi-Atherstone", "Aldi-Atherstone",
            address, "New ref", "PO-676", "Pallets", "CHEP", "Retain instruction", null);

        Assert.IsType<OkObjectResult>(await controller.Update(row.Id, request, CancellationToken.None));
        db.ChangeTracker.Clear();
        var order = Assert.Single(await PlanningRegisterStore.ReadOrdersAsync(db, date, date, CancellationToken.None));
        Assert.DoesNotContain("Old address", order.DriverInstructions);
        Assert.DoesNotContain("Old ref", order.DriverInstructions);
        Assert.Contains("Customer ref: New ref", order.DriverInstructions);
        Assert.Contains("Retain instruction", order.DriverInstructions);
        if (address is null) Assert.DoesNotContain("Delivery address:", order.DriverInstructions);
        else Assert.Contains("Delivery address: " + address, order.DriverInstructions);
        Assert.Equal("Colworth Farm", order.SellerName);
        Assert.Equal(5, order.Pallets);
    }
}
