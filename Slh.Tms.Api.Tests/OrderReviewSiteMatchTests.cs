using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderReviewSiteMatchTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;

    public OrderReviewSiteMatchTests(CustomWebFactory factory) => _factory = factory;

    [Fact]
    public async Task Confirm_delivery_site_adds_alias_that_promotion_can_resolve()
    {
        var siteId = Guid.NewGuid();
        var stagedId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.Add(new Site { Id = siteId, ExternalCode = "SITE-ALIAS", Name = "Canonical Depot", Active = true });
            db.StagedImports.Add(new StagedImport
            {
                Id = stagedId,
                EntityType = "order",
                IdempotencyKey = $"site-match-{Guid.NewGuid():N}",
                PayloadJson = """{"poNumber":"PO-1","customerCode":"CUST","collectionDate":"2026-08-27","stallNumber":"Imported Market Stall"}""",
                Status = StagingStatus.PendingReview,
                Source = "test"
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsJsonAsync($"/api/v1/staging/{stagedId}/confirm-delivery-site", new { siteId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var site = await verifyDb.Sites.AsNoTracking().SingleAsync(row => row.Id == siteId);
        await MasterDetailStore.EnrichSitesAsync(verifyDb, new[] { site }, CancellationToken.None);
        Assert.Contains("Imported Market Stall", site.Aliases);
        using var payload = JsonDocument.Parse("""{"stallNumber":"Imported Market Stall"}""");
        var alignment = await OrderSiteMasterAlignment.ResolveAsync(verifyDb, payload.RootElement, CancellationToken.None);
        Assert.Equal("Canonical Depot", alignment.DeliveryName);
        Assert.Contains(verifyDb.StagedImportEvents, row => row.StagedImportId == stagedId && row.EventType == "DeliverySiteMasterMatched");
    }

    [Fact]
    public async Task Create_destination_site_is_duplicate_safe_and_updates_pending_order()
    {
        var stagedId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(new StagedImport
            {
                Id = stagedId,
                EntityType = "order",
                IdempotencyKey = $"create-site-{Guid.NewGuid():N}",
                PayloadJson = "{\"poNumber\":\"PO-2\",\"customerCode\":\"SUMMERBERRY\",\"collectionDate\":\"2026-10-14\",\"stallNumber\":\"K&N facility\"}",
                Status = StagingStatus.PendingReview,
                Source = "test"
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsJsonAsync($"/api/v1/staging/{stagedId}/create-site", new
        {
            field = "destination",
            name = "K&N facility",
            address = "Building 1235, Eastern Perimeter Road, London Heathrow Airport, Hounslow, TW6 2SQ",
            alias = "K&N facility",
            customerCode = "SUMMERBERRY"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var site = await verifyDb.Sites.AsNoTracking().SingleAsync(row => row.Name == "K&N facility");
        var item = await verifyDb.StagedImports.AsNoTracking().SingleAsync(row => row.Id == stagedId);
        using var payload = JsonDocument.Parse(item.PayloadJson);
        Assert.Equal("K&N facility", payload.RootElement.GetProperty("stallNumber").GetString());
        Assert.Contains(verifyDb.IntegrationMappings, row => row.TmsEntityId == site.Id && row.MappingKind == "SiteAlias");
        Assert.Contains(verifyDb.StagedImportEvents, row => row.StagedImportId == stagedId && row.EventType == "SiteMappingUpdated");
    }

    [Fact]
    public async Task Match_site_rejects_alias_already_owned_by_another_site()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var stagedId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.AddRange(
                new Site { Id = firstId, ExternalCode = "SITE-A", Name = "Canonical A", Active = true },
                new Site { Id = secondId, ExternalCode = "SITE-B", Name = "Canonical B", Active = true });
            db.IntegrationMappings.Add(new IntegrationMapping
            {
                Provider = "InfoMailbox", ExternalKey = "Shared depot", NormalizedExternalValue = "shareddepot",
                MappingKind = "SiteAlias", TmsEntityType = "Site", TmsEntityId = firstId, Active = true
            });
            db.StagedImports.Add(new StagedImport
            {
                Id = stagedId, EntityType = "order", IdempotencyKey = $"match-conflict-{Guid.NewGuid():N}",
                PayloadJson = "{\"poNumber\":\"PO-3\",\"customerCode\":\"SUMMERBERRY\",\"collectionDate\":\"2026-10-14\",\"stallNumber\":\"Shared depot\"}",
                Status = StagingStatus.PendingReview, Source = "test"
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsJsonAsync($"/api/v1/staging/{stagedId}/match-site", new { field = "destination", siteId = secondId, alias = "Shared depot" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
