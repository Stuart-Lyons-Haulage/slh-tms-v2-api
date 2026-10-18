using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlanningWipeTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public PlanningWipeTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Deletes_orders_runs_and_intake_up_to_the_day_and_keeps_later_ones()
    {
        var through = new DateOnly(2019, 6, 18);
        var oldLoad = new Load { Reference = $"W-OLD-{Guid.NewGuid():N}"[..20], PlanningDate = through };
        oldLoad.Stops.Add(new LoadStop { LoadId = oldLoad.Id, Sequence = 1, Name = "Old site" });
        var newLoad = new Load { Reference = $"W-NEW-{Guid.NewGuid():N}"[..20], PlanningDate = through.AddDays(1) };
        var oldOrder = new TransportOrder { Reference = $"WO-OLD-{Guid.NewGuid():N}"[..20], CustomerCode = "WIPE", CollectionDate = through.AddDays(-3), Pallets = 4 };
        var newOrder = new TransportOrder { Reference = $"WO-NEW-{Guid.NewGuid():N}"[..20], CustomerCode = "WIPE", CollectionDate = through.AddDays(1), Pallets = 4 };
        var oldStaged = new StagedImport { EntityType = "order", IdempotencyKey = $"wipe-old-{Guid.NewGuid():N}", Status = StagingStatus.PendingReview, PayloadJson = "{\"customerCode\":\"WIPE\",\"collectionDate\":\"2019-06-17\"}" };
        var newStaged = new StagedImport { EntityType = "order", IdempotencyKey = $"wipe-new-{Guid.NewGuid():N}", Status = StagingStatus.PendingReview, PayloadJson = "{\"customerCode\":\"WIPE\",\"collectionDate\":\"2019-06-19\"}" };
        var oldRegisterRun = new StagedImport { EntityType = "planningload", IdempotencyKey = $"planningload:{Guid.NewGuid():N}", Status = StagingStatus.Promoted, PayloadJson = $"{{\"id\":\"{Guid.NewGuid()}\",\"planningDate\":\"2019-06-18\"}}" };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.AddRange(oldLoad, newLoad, oldOrder, newOrder, oldStaged, newStaged, oldRegisterRun);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var plan = await PlanningWipe.ResolveAsync(db, through, CancellationToken.None);
            Assert.Contains(oldLoad.Id, plan.Loads);
            Assert.DoesNotContain(newLoad.Id, plan.Loads);
            Assert.Contains(oldOrder.Id, plan.Orders);
            Assert.DoesNotContain(newOrder.Id, plan.Orders);
            Assert.Contains(oldStaged.Id, plan.Staged);
            Assert.Contains(oldRegisterRun.Id, plan.Staged);
            Assert.DoesNotContain(newStaged.Id, plan.Staged);
            await PlanningWipe.ExecuteAsync(db, plan, "test", NullLogger.Instance, CancellationToken.None);
        }

        using var check = factory.Services.CreateScope();
        var verify = check.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.False(await verify.Loads.AnyAsync(x => x.Id == oldLoad.Id));
        Assert.False(await verify.LoadStops.AnyAsync(x => x.LoadId == oldLoad.Id));
        Assert.True(await verify.Loads.AnyAsync(x => x.Id == newLoad.Id));
        Assert.False(await verify.TransportOrders.AnyAsync(x => x.Id == oldOrder.Id));
        Assert.True(await verify.TransportOrders.AnyAsync(x => x.Id == newOrder.Id));
        Assert.False(await verify.StagedImports.AnyAsync(x => x.Id == oldStaged.Id || x.Id == oldRegisterRun.Id));
        Assert.True(await verify.StagedImports.AnyAsync(x => x.Id == newStaged.Id));
    }

    [Fact]
    public async Task Needs_an_admin_a_matching_confirmation_and_no_future_date()
    {
        var planner = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.GetAsync("/api/v1/admin/planning-wipe?through=2019-06-18")).StatusCode);

        var admin = factory.CreateClientWithUser("admin@lyonshaulage.com", "Tms.Write,Tms.Approve");
        admin.DefaultRequestHeaders.Add("X-Test-Roles", "TMS.Admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/admin/planning-wipe?through=2019-06-18")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/admin/planning-wipe", new { through = "2019-06-18", confirm = "yes" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/planning-wipe?through=2099-01-01")).StatusCode);
    }
}
