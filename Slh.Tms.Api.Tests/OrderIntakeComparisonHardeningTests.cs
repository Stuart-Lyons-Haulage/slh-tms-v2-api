using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeComparisonHardeningTests
{
    [Fact]
    public void ComparisonGet_UsesReadPolicy()
    {
        var method = typeof(OrderIntakeDuplicateCheckController)
            .GetMethod(nameof(OrderIntakeDuplicateCheckController.CompareStagedOrder), BindingFlags.Instance | BindingFlags.Public)!;
        var policies = method.GetCustomAttributes<AuthorizeAttribute>().Select(attribute => attribute.Policy).ToList();

        Assert.Contains("TmsRead", policies);
        Assert.DoesNotContain("TmsWrite", policies);
    }

    [Fact]
    public void MissingLiveOrderSchema_IsRecognisedAndReturns503InsteadOfNewOrder()
    {
        var schemaError = new InvalidOperationException("Invalid object name 'TransportOrders'.");

        Assert.True(OrderIntakeDuplicateCheckController.IsSchemaUnavailable(schemaError));

        var unavailable = OrderIntakeDuplicateCheckController.LiveOrderComparisonUnavailable();
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        var json = JsonSerializer.Serialize(unavailable.Value);
        Assert.Contains("LiveOrderComparisonUnavailable", json);
        Assert.DoesNotContain("New order", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IncomingNonBlankValue_IsReportedWhenLiveValueIsNull()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var staged = new StagedImport
        {
            EntityType = "order",
            IdempotencyKey = "comparison-null-from",
            Source = "test",
            PayloadJson = JsonSerializer.Serialize(new
            {
                customerCode = "ALDI",
                customerPo = "PO-999",
                poNumber = "PO-999",
                collectionDate = "2026-09-09",
                deliveryDate = "2026-09-09",
                sellerName = "Selsey",
                stallNumber = "Aldi Atherstone",
                pallets = 2
            })
        };
        db.StagedImports.Add(staged);
        db.TransportOrders.Add(new TransportOrder
        {
            Reference = "PO-999",
            CustomerCode = "ALDI",
            CollectionDate = new DateOnly(2026, 9, 9),
            DeliveryDate = new DateOnly(2026, 9, 9),
            SellerName = null,
            StallNumber = "Aldi Atherstone",
            Pallets = 2
        });
        await db.SaveChangesAsync();

        var controller = new OrderIntakeDuplicateCheckController(db, NullLogger<OrderIntakeDuplicateCheckController>.Instance);
        var result = await controller.CompareStagedOrder(staged.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Collection site", json);
        Assert.Contains("Selsey", json);
    }
}
