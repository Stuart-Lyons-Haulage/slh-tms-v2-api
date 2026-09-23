using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
        var geofenceId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.Add(new Site { Id = siteId, ExternalCode = "SITE-ALIAS", Name = "Canonical Depot", Active = true });
            db.SiteGeofences.Add(new SiteGeofence
            {
                Id = geofenceId,
                Name = "Imported Market Stall geofence",
                NormalizedName = "IMPORTEDMARKETSTALLGEOFENCE",
                PolygonJson = "[]",
                Active = true
            });
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
        var response = await client.PostAsJsonAsync($"/api/v1/staging/{stagedId}/confirm-delivery-site", new { siteId, geofenceId });

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
        var geofence = await verifyDb.SiteGeofences.SingleAsync(row => row.Id == geofenceId);
        Assert.Equal(siteId, geofence.SiteId);
        Assert.Equal("SITE-ALIAS", geofence.SiteNumber);

        var auditProcessor = new AuditOutboxProcessor(verifyDb, NullLogger<AuditOutboxProcessor>.Instance);
        await auditProcessor.ProcessPendingAsync(CancellationToken.None);
        Assert.True(await verifyDb.MasterDataAudits.AnyAsync(row =>
            row.EntityType == "Geofence"
            && row.EntityId == geofenceId
            && row.Action == "DeliveryImportSiteConfirmed"));
    }
}
