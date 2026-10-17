using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlannerSourceLineResilientEndpointTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;

    public PlannerSourceLineResilientEndpointTests(CustomWebFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Source_line_schema_imports_through_normal_resilient_endpoint()
    {
        var date = new DateOnly(2026, 8, 24);
        var request = new PlannerPlanImportRequest("slh-planner-plan-v3-source-lines", date, [
            new PlannerPlanRunRequest(
                "3",
                "3",
                "AM",
                date,
                null,
                null,
                null,
                null,
                true,
                "Matched",
                new PlannerPlanSourceRequest("Lyons collections 240826.xlsm", "Collection Plan"),
                [
                    new PlannerPlanStopRequest(1, "NWF-Selsey", "Morrisons-Stockton", 4, "TEST-SEL-4", "Std", "04:30:00", "05:00:00", "18:00:00", 11),
                    new PlannerPlanStopRequest(2, "NWF-Merston", "Morrisons-Stockton", 5, "TEST-MER-5", "Std", "05:00:00", "05:30:00", "18:00:00", 12)
                ])
        ]);

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var response = await client.PostAsJsonAsync("/api/v1/planning/import-plan", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<PlannerPlanImportSummary>();
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.Created + summary.Updated + summary.Unchanged);
        Assert.Empty(summary.Runs.Where(run => run.Outcome.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)));
    }
}
