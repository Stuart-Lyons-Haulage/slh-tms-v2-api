using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

/// <summary>Office actions on a vehicle-check gap are kept per asset and driver for the day.</summary>
public sealed class ComplianceChecksTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public ComplianceChecksTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Paper_check_and_chase_are_recorded_for_the_day()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var asset = Guid.NewGuid();
        var driver = Guid.NewGuid();
        foreach (var kind in new[] { "Chased", "PaperCheck" })
        {
            var response = await client.PostAsync("/api/v1/daily-compliance/checks", new StringContent(JsonSerializer.Serialize(new
            {
                date = "2026-10-18", assetType = "Vehicle", assetId = asset, driverId = driver, kind, note = kind == "PaperCheck" ? "Sheet in tray" : null
            }), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var actions = await ComplianceChecks.ForDateAsync(db, new DateOnly(2026, 10, 18), CancellationToken.None);
        var list = actions[ComplianceChecks.KeyFor("Vehicle", asset, driver)];
        Assert.Equal(new[] { "Chased", "PaperCheck" }, list.Select(item => item.Kind));
        Assert.Equal("Sheet in tray", list[1].Note);
        Assert.Empty(await ComplianceChecks.ForDateAsync(db, new DateOnly(2026, 10, 19), CancellationToken.None));
    }

    [Fact]
    public async Task A_check_needs_the_asset_and_driver()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var response = await client.PostAsync("/api/v1/daily-compliance/checks", new StringContent(JsonSerializer.Serialize(new
        {
            date = "2026-10-18", assetType = "Vehicle", assetId = Guid.Empty, driverId = Guid.Empty, kind = "Chased"
        }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
