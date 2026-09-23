using System.Security.Claims;
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

public sealed class RoadrunnerSiteReviewControllerTests
{
    private const string ReviewType = "masterdata:roadrunner-site-review";

    [Fact]
    public async Task Reconciliation_stages_postcode_suggestion_without_mutating_canonical_site_and_preserves_historical_payload()
    {
        await using var db = CreateDb();
        var site = Site("SITE001", "Canonical Name", "1 Depot Way, AB1 2CD");
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        var controller = Lookups(db);

        await controller.ReconcileRoadrunnerSites([Profile("RR-01", "Suggested Name", "AB1 2CD")], CancellationToken.None);

        Assert.Null(site.RoadrunnerCode);
        Assert.Equal("Canonical Name", site.Name);
        var proposal = Assert.Single(db.StagedImports.Where(x => x.EntityType == ReviewType));
        Assert.Equal(StagingStatus.PendingReview, proposal.Status);
        Assert.Contains("Unique postcode", proposal.PayloadJson);

        proposal.Status = StagingStatus.Rejected;
        proposal.ReviewNote = "Operator rejected the suggestion.";
        var historicalPayload = proposal.PayloadJson;
        await db.SaveChangesAsync();

        await controller.ReconcileRoadrunnerSites([Profile("RR-01", "Changed source name", "AB1 2CD")], CancellationToken.None);

        var historical = await db.StagedImports.SingleAsync(x => x.Id == proposal.Id);
        Assert.Equal(historicalPayload, historical.PayloadJson);
        Assert.Equal("Operator rejected the suggestion.", historical.ReviewNote);
    }

    [Fact]
    public async Task Reconciliation_reports_existing_explicit_Roadrunner_link_without_creating_proposal()
    {
        await using var db = CreateDb();
        var site = Site("SITE001", "Canonical Name");
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        await MasterDetailStore.SaveAsync(db, "site", site.ExternalCode,
            """{"externalCode":"SITE001","roadrunnerCode":"RR-01"}""", "test", "tester", CancellationToken.None);

        var result = await Lookups(db).ReconcileRoadrunnerSites([Profile("RR-01", "Source Name", null)], CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"linked\":1", json);
        Assert.Empty(db.StagedImports.Where(x => x.EntityType == ReviewType));
    }

    [Fact]
    public async Task Link_only_preserves_canonical_fields_and_records_review_audit_and_event()
    {
        await using var db = CreateDb();
        var site = Site("SITE001", "Keep Me", "Original address");
        var review = Review("RR-01", "Replacement Name");
        db.AddRange(site, review);
        await db.SaveChangesAsync();

        var result = await ReviewController(db).LinkOnly(review.Id, new RoadrunnerSiteLinkRequest(site.Id, null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Keep Me", site.Name);
        Assert.Equal("Original address", site.CollectionAddress);
        Assert.Equal(StagingStatus.Promoted, review.Status);
        Assert.Single(db.AuditOutboxes.Where(x => x.Payload.Contains("RoadrunnerLinked")));
        Assert.Single(db.StagedImportEvents.Where(x => x.EventType == "RoadrunnerLinked"));
        Assert.Equal("RR-01", (await EnrichedSite(db, site.Id)).RoadrunnerCode);
    }

    [Fact]
    public async Task Link_rejects_Roadrunner_code_already_linked_to_inactive_site()
    {
        await using var db = CreateDb();
        var active = Site("SITE001", "Active target");
        var inactive = Site("SITE002", "Inactive historical", active: false);
        var review = Review("RR-01", "Source Name");
        db.AddRange(active, inactive, review);
        await db.SaveChangesAsync();
        await MasterDetailStore.SaveAsync(db, "site", inactive.ExternalCode,
            """{"externalCode":"SITE002","roadrunnerCode":"RR-01"}""", "test", "tester", CancellationToken.None);

        var result = await ReviewController(db).LinkOnly(review.Id, new RoadrunnerSiteLinkRequest(active.Id, null), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StagingStatus.PendingReview, review.Status);
        Assert.Empty(db.AuditOutboxes);
    }

    [Fact]
    public async Task Add_alias_deduplicates_case_insensitively_while_linking_identity()
    {
        await using var db = CreateDb();
        var site = Site("SITE001", "Canonical", aliases: "Source Name; Other");
        var review = Review("RR-01", "source name");
        db.AddRange(site, review);
        await db.SaveChangesAsync();

        var result = await ReviewController(db).AddAlias(review.Id, new RoadrunnerSiteAliasRequest(site.Id, null, null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var enriched = await EnrichedSite(db, site.Id);
        Assert.Equal("Source Name; Other", enriched.Aliases);
        Assert.Equal("RR-01", enriched.RoadrunnerCode);
        Assert.Equal(StagingStatus.Promoted, review.Status);
    }

    [Fact]
    public async Task Accept_selected_fields_changes_only_explicitly_selected_canonical_fields()
    {
        await using var db = CreateDb();
        var site = Site("SITE001", "Old Name", "Keep address", aliases: "Keep alias");
        var review = Review("RR-01", "New Name", "New address");
        db.AddRange(site, review);
        await db.SaveChangesAsync();

        var result = await ReviewController(db).AcceptSelectedFields(review.Id,
            new RoadrunnerSiteAcceptFieldsRequest(site.Id, ["Name"], null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("New Name", site.Name);
        Assert.Equal("Keep address", site.CollectionAddress);
        var enriched = await EnrichedSite(db, site.Id);
        Assert.Equal("Keep alias", enriched.Aliases);
        Assert.Equal("RR-01", enriched.RoadrunnerCode);
    }

    [Fact]
    public async Task Dismiss_changes_review_only_and_never_creates_master_detail_or_audit()
    {
        await using var db = CreateDb();
        var review = Review("RR-01", "Source Name");
        db.StagedImports.Add(review);
        await db.SaveChangesAsync();

        var result = await ReviewController(db).Dismiss(review.Id, new RoadrunnerSiteDismissRequest(null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(StagingStatus.Rejected, review.Status);
        Assert.Empty(db.Sites);
        Assert.Empty(db.StagedImports.Where(x => x.EntityType == "masterdetail:site"));
        Assert.Empty(db.AuditOutboxes);
        Assert.Single(db.StagedImportEvents.Where(x => x.EventType == "RoadrunnerDismissed"));
    }

    [Fact]
    public async Task Unmatched_proposal_can_create_canonical_SITE_code_without_using_Roadrunner_code()
    {
        await using var db = CreateDb();
        db.Sites.AddRange(Site("SITE001", "First"), Site("SITE003", "Third"));
        var review = Review("RR-99", "New Roadrunner Site", "9 New Street");
        db.StagedImports.Add(review);
        await db.SaveChangesAsync();

        var result = await ReviewController(db).CreateSite(review.Id,
            new RoadrunnerSiteCreateRequest(null, null, null, null, null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var created = await db.Sites.SingleAsync(x => x.Name == "New Roadrunner Site");
        Assert.Equal("SITE002", created.ExternalCode);
        Assert.NotEqual("RR-99", created.ExternalCode);
        Assert.Equal("RR-99", (await EnrichedSite(db, created.Id)).RoadrunnerCode);
        Assert.Equal(StagingStatus.Promoted, review.Status);
        Assert.Single(db.AuditOutboxes.Where(x => x.Payload.Contains("CreatedFromRoadrunnerReview")));
    }

    private static RoadrunnerSiteReviewController ReviewController(TmsDbContext db) => new(db)
    {
        ControllerContext = new ControllerContext { HttpContext = UserContext() }
    };

    private static LookupsController Lookups(TmsDbContext db) => new(db, NullLogger<LookupsController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = UserContext() }
    };

    private static DefaultHttpContext UserContext()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("name", "tester")], "test"));
        return context;
    }

    private static Site Site(string code, string name, string? address = null, bool active = true, string? aliases = null) => new()
    {
        ExternalCode = code,
        Name = name,
        CollectionAddress = address,
        Aliases = aliases,
        Active = active
    };

    private static StagedImport Review(string code, string company, string? address = null) => new()
    {
        EntityType = ReviewType,
        IdempotencyKey = $"{ReviewType}:{code}",
        PayloadJson = JsonSerializer.Serialize(new
        {
            roadRunner = new { Code = code, Company = company, Add1 = address }
        })
    };

    private static RoadrunnerSiteProfileRequest Profile(string code, string company, string? postcode) =>
        JsonSerializer.Deserialize<RoadrunnerSiteProfileRequest>(JsonSerializer.Serialize(new
        {
            Code = code,
            Company = company,
            Add1 = "1 Source Way",
            AddPostcode = postcode
        }))!;

    private static async Task<Site> EnrichedSite(TmsDbContext db, Guid id)
    {
        db.ChangeTracker.Clear();
        var site = await db.Sites.AsNoTracking().SingleAsync(x => x.Id == id);
        await MasterDetailStore.EnrichSitesAsync(db, [site], CancellationToken.None);
        return site;
    }

    private static TmsDbContext CreateDb() => new(new DbContextOptionsBuilder<TmsDbContext>()
        .UseInMemoryDatabase($"roadrunner-review-{Guid.NewGuid():N}").Options);
}
