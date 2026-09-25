using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RoadrunnerReviewRetirementTests
{
    [Fact]
    public async Task Roadrunner_reconcile_never_creates_site_review_staging()
    {
        await using var db = new TmsDbContext(new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"roadrunner-retired-{Guid.NewGuid():N}")
            .Options);

        db.Sites.Add(new Site
        {
            ExternalCode = "SITE001",
            Name = "Existing Site",
            CollectionAddress = "1 Existing Road, PO1 1AA",
            Active = true
        });
        await db.SaveChangesAsync();

        var controller = new LookupsController(db, NullLogger<LookupsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("name", "tester")], "test"))
                }
            }
        };

        var profile = JsonSerializer.Deserialize<RoadrunnerSiteProfileRequest>(JsonSerializer.Serialize(new
        {
            Code = "RR-NEW",
            Company = "Unlinked Roadrunner Site",
            Add1 = "2 New Road",
            AddPostcode = "PO2 2BB"
        }))!;

        var result = await controller.ReconcileRoadrunnerSites([profile], CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"review\":0", json);
        Assert.Contains("\"unmatched\":1", json);
        Assert.DoesNotContain(db.StagedImports, row =>
            string.Equals(row.EntityType, "masterdata:roadrunner-site-review", StringComparison.OrdinalIgnoreCase));
    }
}
