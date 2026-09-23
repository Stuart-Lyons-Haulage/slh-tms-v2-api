using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlannerPlanImportTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;

    public PlannerPlanImportTests(CustomWebFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    [Fact]
    public void Mixed_capacity_uses_26_standard_and_33_euro()
    {
        var run = Run(new DateOnly(2026, 8, 18), "COL-01", true, [
            Stop(1, 13, "Std"),
            Stop(2, 16, "Euro")
        ]);
        var result = PlannerPlanImportRules.Capacity(run);
        Assert.Equal("Green", result.Status);
        Assert.Equal(98.5m, result.UtilisationPercent);
    }

    [Theory]
    [InlineData("00:00:00", "Run 1 AM")]
    [InlineData("11:59:59", "Run 1 AM")]
    [InlineData("12:00:00", "Run 1 PM")]
    [InlineData("23:59:59", "Run 1 PM")]
    public void Planner_run_label_uses_collection_time_period_boundaries(string collectFrom, string expected)
    {
        var date = new DateOnly(2026, 8, 20);
        var run = new PlannerPlanRunRequest("W1-01", "1", "Wave 1", date, null, null, null, null, true, "Matched",
            new PlannerPlanSourceRequest("planner.xlsm", "Collection Plan"), [
                new PlannerPlanStopRequest(1, "Collection", "Delivery", 10, "REF-1", null, collectFrom, null, null, 2)
            ]);

        Assert.Equal(expected, PlannerPlanImportRules.PlannerRunLabel(run));
    }

    [Fact]
    public async Task Import_is_idempotent_and_held_runs_are_not_created()
    {
        var date = new DateOnly(2026, 8, 18);
        await SeedMasterData();
        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var request = new PlannerPlanImportRequest("slh-planner-plan-v2", date, [
            Run(date, "COL-01", true, [Stop(1, 13, "Std"), Stop(2, 16, "Euro")], driver: "Test Driver", vehicle: "ABC", trailer: "22"),
            Run(date, "S3", false, [Stop(1, 20, "Euro")], reconciliation: "HOLD - MAILBOX_CANCELLATION")
        ]);

        var first = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstSummary = await first.Content.ReadFromJsonAsync<PlannerPlanImportSummary>();
        Assert.NotNull(firstSummary);
        Assert.Equal(1, firstSummary!.Created);
        Assert.Equal(1, firstSummary.Held);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            Assert.True(db.Loads.Count() == 1, $"Expected one load after first import, found {db.Loads.Count()}.");
            Assert.True(db.StagedImports.Count(x => x.IdempotencyKey == "planimport:20260818:COL-01") == 1,
                $"Expected one audit marker after first import, found {db.StagedImports.Count(x => x.IdempotencyKey == "planimport:20260818:COL-01")}.");
        }

        var second = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondSummary = await second.Content.ReadFromJsonAsync<PlannerPlanImportSummary>();
        Assert.NotNull(secondSummary);
        Assert.True(secondSummary!.Updated + secondSummary.Unchanged == 1,
            $"Second import counts: Created={secondSummary.Created}, Updated={secondSummary.Updated}, Unchanged={secondSummary.Unchanged}, Held={secondSummary.Held}; outcomes={string.Join(",", secondSummary.Runs.Select(x => x.Outcome))}");
        Assert.Equal(1, secondSummary.Held);

        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Single(finalDb.Loads);
        var load = finalDb.Loads.Single();
        Assert.Equal("PLAN-20260818-COL-01", load.Reference);
        Assert.Equal(LoadStatus.Planned, load.Status);
        Assert.Equal(2, finalDb.LoadStops.Count());
        Assert.DoesNotContain(finalDb.Loads, x => x.Reference.Contains("S3"));
        Assert.Equal(1, finalDb.StagedImports.Count(x => x.IdempotencyKey == "planimport:20260818:COL-01" && x.Status == StagingStatus.Promoted));
        Assert.Equal(0, finalDb.StagedImports.Count(x => x.IdempotencyKey == "planimport:20260818:S3"));
    }

    [Fact]
    public async Task Import_writes_pallet_control_allocations_for_matched_orders()
    {
        var date = new DateOnly(2026, 8, 20);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.TransportOrders.AddRange(
                new TransportOrder { Reference = "REF-1", CustomerCode = "NWF", CollectionDate = date, Pallets = 11, SellerName = "NWF-Merston", StallNumber = "Aldi-Darlington" },
                new TransportOrder { Reference = "REF-2", CustomerCode = "NWF", CollectionDate = date, Pallets = 6, SellerName = "NWF-Merston", StallNumber = "Morrisons-Stockton" });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var request = new PlannerPlanImportRequest("slh-planner-plan-v2", date, [
            new PlannerPlanRunRequest("W1-01", "1", "Wave 1", date, null, null, null, null, true, "Matched",
                new PlannerPlanSourceRequest("planner.xlsm", "Collection Plan"), [
                    new PlannerPlanStopRequest(1, "NWF-Merston", "Aldi-Darlington", 11, "REF-1", null, "04:30:00", "05:00:00", "18:00:00", 4),
                    new PlannerPlanStopRequest(2, "NWF-Merston", "Morrisons-Stockton", 6, "REF-2", null, "04:30:00", "05:00:00", "18:00:00", 5)
                ])
        ]);

        var response = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var allocations = finalDb.StagedImports.Where(row => row.EntityType == PlanningAllocationStore.EntityType).ToList();
        Assert.Equal(2, allocations.Count);
        Assert.Equal(17, allocations.Sum(row => JsonSerializer.Deserialize<AllocationState>(row.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Pallets));
        var load = finalDb.Loads.Single(row => row.Reference == "PLAN-20260820-W1-01");
        await LoadCommercialStore.EnrichAsync(finalDb, [load], CancellationToken.None);
        Assert.Contains("Planner run: Run 1 AM", load.PlannerNotes);
    }

    [Fact]
    public async Task Unresolved_allocations_leave_run_draft_without_failing_import()
    {
        var date = new DateOnly(2026, 8, 19);
        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var request = new PlannerPlanImportRequest("slh-planner-plan-v2", date, [
            Run(date, "S10", true, [Stop(1, 10, "Euro")], driver: "Missing Driver", vehicle: "ZZZ", trailer: "999")
        ]);

        var response = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<PlannerPlanImportSummary>();
        Assert.NotNull(summary);
        Assert.Contains("Missing Driver", summary!.UnresolvedDrivers);
        Assert.Contains("ZZZ", summary.UnresolvedVehicles);
        Assert.Contains("999", summary.UnresolvedTrailers);
        Assert.Equal("ImportedDraft", summary.Runs.Single().Outcome);
    }

    [Fact]
    public async Task Red_capacity_warns_but_does_not_block_import()
    {
        var date = new DateOnly(2026, 8, 20);
        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var request = new PlannerPlanImportRequest("slh-planner-plan-v2", date, [
            Run(date, "COL-23", true, [Stop(1, 38, "Std")])
        ]);

        var response = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<PlannerPlanImportSummary>();
        Assert.NotNull(summary);
        Assert.Equal("Red", summary!.Runs.Single().CapacityStatus);
        Assert.Equal(146.2m, summary.Runs.Single().UtilisationPercent);
        Assert.Contains(summary.Warnings, warning => warning.Contains("COL-23"));
    }

    private async Task SeedMasterData()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Drivers.Add(new Driver { EmployeeNumber = "T001", DisplayName = "Test Driver", Active = true });
        db.Vehicles.Add(new Vehicle { Registration = "AB12ABC", Abbreviation = "ABC", Active = true });
        db.Trailers.Add(new Trailer { TrailerNumber = "22", StandardCapacity = 26, EuroCapacity = 33, Active = true });
        await db.SaveChangesAsync();
    }

    private static PlannerPlanRunRequest Run(DateOnly date, string runRef, bool include, List<PlannerPlanStopRequest> stops,
        string? driver = null, string? vehicle = null, string? trailer = null, string? reconciliation = "Matched planner plan") =>
        new(runRef, runRef, "Collection", date, driver, vehicle, trailer, null, include, reconciliation,
            new PlannerPlanSourceRequest("planner.xlsm", "Plan"), stops);

    private static PlannerPlanStopRequest Stop(int sequence, decimal pallets, string palletType) =>
        new(sequence, "Collection", "Delivery", pallets, $"REF-{sequence}", palletType, null, null, null, sequence + 1);

    private sealed record AllocationState(Guid OrderId, Guid LoadId, int Pallets, DateOnly Date, DateTimeOffset UpdatedAtUtc, string? UpdatedBy);
}