using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TvWallboardAccessTests
{
    private const string DisplayKey = "office-display-key-2026-08-20";
    private const string TestEndpointKey = "test-tv-wallboard-key-20260824";

    [Fact]
    public void Configured_display_key_allows_wallboard_read()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[TvWallboardAccess.HeaderName] = DisplayKey;

        Assert.True(TvWallboardAccess.IsAllowed(context, Configuration(DisplayKey)));
    }

    [Fact]
    public void Missing_or_wrong_display_key_is_rejected()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[TvWallboardAccess.HeaderName] = "wrong-display-key-2026-08-20";

        Assert.False(TvWallboardAccess.IsAllowed(context, Configuration(DisplayKey)));
    }

    [Fact]
    public void Authenticated_portal_user_is_allowed_without_optional_email_claims()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "test-user-object-id")
            ], "Bearer"))
        };

        Assert.True(TvWallboardAccess.IsAllowed(context, Configuration(null)));
    }

    [Fact]
    public async Task Tv_live_runs_accepts_legacy_wallboard_key()
    {
        await using var factory = new CustomWebFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tv-display/live-runs?date=2026-08-24");
        request.Headers.Add(TvWallboardAccess.HeaderName, TestEndpointKey);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Tv_live_runs_retains_planned_runs_and_reports_final_destination_eta_target()
    {
        await using var factory = new CustomWebFactory();
        var loadId = Guid.NewGuid();
        var day = new DateOnly(2026, 8, 24);
        var load = new Load
        {
            Id = loadId,
            Reference = "TV-RUN-1",
            PlanningDate = day,
            Status = LoadStatus.Planned,
            Stops =
            [
                new LoadStop { Id = Guid.NewGuid(), LoadId = loadId, Sequence = 1, Name = "Collection", PlannedArrivalUtc = new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.Zero) },
                new LoadStop { Id = Guid.NewGuid(), LoadId = loadId, Sequence = 2, Name = "Final RDC", PlannedArrivalUtc = new DateTimeOffset(2026, 8, 24, 12, 30, 0, TimeSpan.Zero) }
            ]
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "planningload",
                IdempotencyKey = $"planningload:{loadId:N}",
                PayloadJson = JsonSerializer.Serialize(load, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Source = "test planning register",
                Status = StagingStatus.Promoted
            });
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tv-display/live-runs?date={day:yyyy-MM-dd}");
        request.Headers.Add(TvWallboardAccess.HeaderName, TestEndpointKey);

        using var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("runCount").GetInt32());
        var run = document.RootElement.GetProperty("runs")[0];
        Assert.Equal(loadId, run.GetProperty("id").GetGuid());
        Assert.Equal("Collection", run.GetProperty("nextStop").GetString());
        Assert.Equal("Final RDC", run.GetProperty("finalStop").GetString());
        Assert.Equal("Final RDC", run.GetProperty("etaTarget").GetString());
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 12, 30, 0, TimeSpan.Zero), run.GetProperty("etaUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task Tv_live_runs_keeps_live_table_runs_when_planning_register_also_has_rows()
    {
        await using var factory = new CustomWebFactory();
        var day = new DateOnly(2026, 8, 24);
        var liveLoadId = Guid.NewGuid();
        var registerLoadId = Guid.NewGuid();
        var liveLoad = new Load
        {
            Id = liveLoadId,
            Reference = "LIVE-TABLE-RUN",
            PlanningDate = day,
            Status = LoadStatus.Planned,
            Stops =
            [
                new LoadStop { Id = Guid.NewGuid(), LoadId = liveLoadId, Sequence = 1, Name = "Live collection", PlannedArrivalUtc = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero) },
                new LoadStop { Id = Guid.NewGuid(), LoadId = liveLoadId, Sequence = 2, Name = "Live final", PlannedArrivalUtc = new DateTimeOffset(2026, 8, 24, 13, 0, 0, TimeSpan.Zero) }
            ]
        };
        var registerLoad = new Load
        {
            Id = registerLoadId,
            Reference = "REGISTER-RUN",
            PlanningDate = day,
            Status = LoadStatus.Planned,
            Stops =
            [
                new LoadStop { Id = Guid.NewGuid(), LoadId = registerLoadId, Sequence = 1, Name = "Register collection", PlannedArrivalUtc = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero) },
                new LoadStop { Id = Guid.NewGuid(), LoadId = registerLoadId, Sequence = 2, Name = "Register final", PlannedArrivalUtc = new DateTimeOffset(2026, 8, 24, 14, 0, 0, TimeSpan.Zero) }
            ]
        };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Loads.Add(liveLoad);
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "planningload",
                IdempotencyKey = $"planningload:{registerLoadId:N}",
                PayloadJson = JsonSerializer.Serialize(registerLoad, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Source = "test planning register",
                Status = StagingStatus.Promoted
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tv-display/live-runs?date={day:yyyy-MM-dd}");
        request.Headers.Add(TvWallboardAccess.HeaderName, TestEndpointKey);

        using var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("runCount").GetInt32());
        var ids = document.RootElement.GetProperty("runs").EnumerateArray().Select(run => run.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(liveLoadId, ids);
        Assert.Contains(registerLoadId, ids);
    }

    private static IConfiguration Configuration(string? displayKey) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(displayKey is null
                ? []
                : new Dictionary<string, string?> { ["TvWallboard:AccessKey"] = displayKey })
            .Build();
}
