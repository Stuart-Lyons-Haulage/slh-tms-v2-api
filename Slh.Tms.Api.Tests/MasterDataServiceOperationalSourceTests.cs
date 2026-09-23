using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MasterDataServiceOperationalSourceTests
{
    [Fact]
    public async Task Vehicles_fuel_cards_and_markets_come_from_the_operational_SQL_tables()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"master-operational-source-{Guid.NewGuid():N}")
            .Options;
        await using var db = new TmsDbContext(options);
        db.Vehicles.Add(new Vehicle
        {
            Registration = "SLH123", FleetNumber = "FLEET-1", FuelProvider = "Shell",
            FuelPin = "1234", ShellCard = "CARD1", Active = true
        });
        db.Vehicles.Add(new Vehicle { Registration = "OLD123", Active = false });
        db.MarketContacts.Add(new MarketContact
        {
            MarketKey = "market-item-1", Market = "Covent", Name = "Seller One",
            StandOrLocation = "Stand 10", Salesman = "Sales One", Sender = "Sender One", Active = true
        });
        await db.SaveChangesAsync();

        var service = new MasterDataService(db, new MemoryCache(new MemoryCacheOptions()));

        var vehicles = await service.GetActiveVehiclesAsync();
        var fuelCards = await service.GetActiveFuelCardsAsync();
        var markets = await service.GetActiveMarketsAsync();

        var vehicle = Assert.Single(vehicles);
        Assert.Equal("SLH123", vehicle.Registration);
        Assert.Equal("1234", vehicle.FuelPin);
        var fuelCard = Assert.Single(fuelCards);
        Assert.Equal(vehicle.VehicleId, fuelCard.VehicleId);
        Assert.Equal("Shell", fuelCard.FuelProvider);
        var market = Assert.Single(markets);
        Assert.Equal("market-item-1", market.MarketId);
        Assert.Equal("Stand 10", market.StandOrLocation);
        Assert.Equal("Sales One", market.Salesman);
    }

    [Fact]
    public async Task Market_editor_updates_the_SQL_master_and_audits_the_change()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"market-update-{Guid.NewGuid():N}")
            .Options;
        await using var db = new TmsDbContext(options);
        var contact = new MarketContact { Market = "Covent", Name = "Seller One", Active = true };
        db.MarketContacts.Add(contact);
        await db.SaveChangesAsync();

        var controller = new LookupsController(db, NullLogger<LookupsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("name", "tester")], "test"));

        var result = await controller.UpdateMarketContact(contact.Id, new MarketContactUpdateRequest(
            "Spit", "Seller One", "Stand 3", "Sales Two", "Sender Two", true), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Spit", contact.Market);
        Assert.Equal("Stand 3", contact.StandOrLocation);
        Assert.Equal("Sales Two", contact.Salesman);
        Assert.False(string.IsNullOrWhiteSpace(contact.MarketKey));
        var audit = Assert.Single(db.AuditOutboxes);
        Assert.Equal(AuditOutboxEventTypes.MasterDataAudit, audit.EventType);
    }

    [Fact]
    public async Task Assignment_compliance_reads_expiry_dates_from_operational_SQL_rows()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"master-compliance-{Guid.NewGuid():N}")
            .Options;
        await using var db = new TmsDbContext(options);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var driver = new Driver
        {
            EmployeeNumber = "TM-101", DisplayName = "Driver One", LicenceExpiry = today.AddDays(-1),
            CPCExpiry = today.AddDays(15), Active = true
        };
        var vehicle = new Vehicle
        {
            Registration = "SLH123", MOTExpiry = today.AddDays(10), Active = true
        };
        db.Drivers.Add(driver);
        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync();

        var result = await new MasterAssignmentComplianceService(db).CheckAsync(driver.Id, vehicle.Id, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Contains(result.Errors, error => error.Contains("driver licence", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("driver CPC", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("vehicle MOT", StringComparison.Ordinal));
    }
}
