using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class CommercialRetirementTests
{
    [Fact]
    public async Task Legacy_commercial_rows_only_rehydrate_operational_values()
    {
        await using var db = CreateDb();
        var load = NewLoad();
        db.Loads.Add(load);
        db.StagedImports.Add(new StagedImport
        {
            EntityType = "loadcommercial",
            IdempotencyKey = $"loadcommercial:{load.Id:N}",
            PayloadJson = JsonSerializer.Serialize(new LoadCommercialValues(
                RevenueAmount: 1200m,
                FuelSurchargeAmount: 100m,
                EstimatedCostAmount: 800m,
                ActualCostAmount: 850m,
                EstimatedDistanceMiles: 250m,
                EmptyMiles: 42m,
                InvoiceStatus: "Ready",
                CommercialNotes: "Legacy margin note",
                PalletSpacesUsed: 18m,
                TotalPalletSpaces: 26m,
                CapacityType: "Standard pallets",
                DepotSplits: "12/6",
                TemperatureC: 4m,
                PlannerNotes: "Operational note")) ,
            Source = "legacy",
            Status = StagingStatus.Promoted
        });
        await db.SaveChangesAsync();

        load.RevenueAmount = null;
        load.FuelSurchargeAmount = null;
        load.EstimatedCostAmount = null;
        load.ActualCostAmount = null;
        load.EstimatedDistanceMiles = null;
        load.EmptyMiles = null;
        load.InvoiceStatus = null;
        load.CommercialNotes = null;
        load.PalletSpacesUsed = null;
        load.TotalPalletSpaces = null;
        load.CapacityType = null;
        load.DepotSplits = null;
        load.TemperatureC = null;
        load.PlannerNotes = null;

        await LoadCommercialStore.EnrichAsync(db, [load], CancellationToken.None);

        Assert.Null(load.RevenueAmount);
        Assert.Null(load.FuelSurchargeAmount);
        Assert.Null(load.EstimatedCostAmount);
        Assert.Null(load.ActualCostAmount);
        Assert.Null(load.EstimatedDistanceMiles);
        Assert.Null(load.InvoiceStatus);
        Assert.Null(load.CommercialNotes);
        Assert.Equal(42m, load.EmptyMiles);
        Assert.Equal(18m, load.PalletSpacesUsed);
        Assert.Equal(26m, load.TotalPalletSpaces);
        Assert.Equal("Standard pallets", load.CapacityType);
        Assert.Equal("12/6", load.DepotSplits);
        Assert.Equal(4m, load.TemperatureC);
        Assert.Equal("Operational note", load.PlannerNotes);
    }

    [Fact]
    public async Task Compatibility_save_writes_only_operational_run_data()
    {
        await using var db = CreateDb();
        var load = NewLoad();
        db.Loads.Add(load);
        await db.SaveChangesAsync();

        await LoadCommercialStore.SaveAsync(db, load, new LoadCommercialValues(
            RevenueAmount: 1200m,
            FuelSurchargeAmount: 100m,
            EstimatedCostAmount: 800m,
            ActualCostAmount: 850m,
            EstimatedDistanceMiles: 250m,
            EmptyMiles: 37m,
            InvoiceStatus: "Ready",
            CommercialNotes: "Must not persist",
            PalletSpacesUsed: 20m,
            TotalPalletSpaces: 26m,
            CapacityType: "Standard pallets",
            DepotSplits: null,
            TemperatureC: null,
            PlannerNotes: "Keep this"), "test", CancellationToken.None);

        var row = await db.StagedImports.SingleAsync(x => x.IdempotencyKey == $"runoperational:{load.Id:N}");
        Assert.Equal("runoperational", row.EntityType);
        Assert.DoesNotContain("revenueAmount", row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fuelSurchargeAmount", row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("estimatedCostAmount", row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actualCostAmount", row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invoiceStatus", row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("commercialNotes", row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("emptyMiles", row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(37m, load.EmptyMiles);
        Assert.Equal(20m, load.PalletSpacesUsed);
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TmsDbContext(options);
    }

    private static Load NewLoad() => new()
    {
        Reference = $"RUN-{Guid.NewGuid():N}",
        PlanningDate = new DateOnly(2026, 8, 23),
        Status = LoadStatus.Draft
    };
}
