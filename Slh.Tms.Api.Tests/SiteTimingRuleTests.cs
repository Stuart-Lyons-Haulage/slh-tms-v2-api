using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

/// <summary>
/// SiteTimingRuleStore.ReadAsync used to load every Promoted StagedImport row (orders, email
/// evidence, driver/tacho/sage syncs - everything) and filter to site-timing rows in memory.
/// That full-table load got slow enough, once StagedImports had grown, to fail outright - and
/// the failure was swallowed and reported as "no rules", which silently guessed every route's
/// collection/delivery time instead of using Site Master. This pins the fix: the EntityType
/// filter runs in SQL, and unrelated rows (of any status/type) never affect the result.
/// </summary>
public sealed class SiteTimingRuleStoreTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public SiteTimingRuleStoreTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task ReadAsync_returns_only_promoted_site_timing_rows_among_unrelated_staged_imports()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var routeCombination = $"TEST-ROUTE-{Guid.NewGuid():N}";

        db.StagedImports.AddRange(
            new StagedImport
            {
                EntityType = "masterdetail:sitetimingrule",
                IdempotencyKey = $"rule-{Guid.NewGuid():N}",
                Status = StagingStatus.Promoted,
                PayloadJson = JsonSerializer.Serialize(new { routeCombination, collectionKey = "NWF", deliveryKey = "Gadbrook", collectionContext = "Selsey", palletType = "Std", lastDespatch = "07:00", collectFrom = "06:00", collectTo = "07:00", depotDeadline = "18:00" })
            },
            // A rule row that is not yet Promoted must not be returned.
            new StagedImport
            {
                EntityType = "masterdetail:sitetimingrule",
                IdempotencyKey = $"rule-pending-{Guid.NewGuid():N}",
                Status = StagingStatus.PendingReview,
                PayloadJson = JsonSerializer.Serialize(new { routeCombination = $"PENDING-{Guid.NewGuid():N}" })
            },
            // Unrelated Promoted rows of other entity types must not be matched or scanned.
            new StagedImport { EntityType = "order", IdempotencyKey = $"order-{Guid.NewGuid():N}", Status = StagingStatus.Promoted, PayloadJson = "{}" },
            new StagedImport { EntityType = "email-evidence", IdempotencyKey = $"evidence-{Guid.NewGuid():N}", Status = StagingStatus.Promoted, PayloadJson = "{}" });
        await db.SaveChangesAsync();

        var store = new SiteTimingRuleStore(db, NullLogger<SiteTimingRuleStore>.Instance);
        var rules = await store.ReadAsync(CancellationToken.None);

        Assert.Contains(rules, rule => rule.RouteCombination == routeCombination && rule.CollectionKey == "NWF" && rule.DeliveryKey == "Gadbrook" && rule.CollectionContext == "Selsey");
        Assert.DoesNotContain(rules, rule => rule.RouteCombination.StartsWith("PENDING-", StringComparison.Ordinal));
    }
}

public sealed class SiteTimingRuleTests
{
    [Fact]
    public void Match_uses_site_master_codes_and_route_combination()
    {
        var sites = new[]
        {
            new Site { ExternalCode = "NWF-Mer", Name = "NWF - Merston", DriverTextName = "NWF - Merston" },
            new Site { ExternalCode = "MOR07", Name = "Morrisons-Wakefield", DriverTextName = "Morrisons-Wakefield" }
        };
        var rule = new SiteTimingRule("NWF-Mer-Morrisons-Wakefield", "Std", "07:00", "06:00", "07:00", "18:00");

        var matched = SiteTimingRuleMatcher.Match(rule, "NWF - Merston", "Morrisons-Wakefield", "Standard", sites);

        Assert.True(matched);
    }

    [Fact]
    public void Match_uses_stored_keys_without_NWF_site_master_and_distinguishes_collection_depots()
    {
        var selsey = new SiteTimingRule("NWF-Sel-Morrisons-Gadbrook", "Std", "07:00", "05:00", "06:00", "18:00", "NWF", "Gadbrook", "Selsey");
        var drayton = new SiteTimingRule("NWF-Dra-Morrisons-Gadbrook", "Std", "07:00", "06:00", "07:00", "18:00", "NWF", "Gadbrook", "Drayton");
        Assert.True(SiteTimingRuleMatcher.Match(selsey, "NWFSELSEY", "Morrisons Gadbrook", "Std", []));
        Assert.False(SiteTimingRuleMatcher.Match(drayton, "NWFSELSEY", "Morrisons Gadbrook", "Std", []));
        Assert.True(SiteTimingRuleMatcher.Match(drayton, "NWFDRAYTON", "Morrisons Gadbrook", "Std", []));
    }

    [Fact]
    public void Match_does_not_confuse_produce_and_standard_Gadbrook()
    {
        var produce = new SiteTimingRule("NWF-Sel-Morrisons-Gadbrook Produce", "Std", "07:00", "05:00", "06:00", "16:00", "NWF", "Gadbrook Produce", "Selsey");
        Assert.False(SiteTimingRuleMatcher.Match(produce, "NWFSELSEY", "Morrisons Gadbrook", "Std", []));
        Assert.True(SiteTimingRuleMatcher.Match(produce, "NWFSELSEY", "Morrisons Gadbrook Produce", "Std", []));
    }

    [Fact]
    public void Window_uses_UK_local_time_and_supports_after_midnight_deadline()
    {
        var rule = new SiteTimingRule("NWF-Mer-Booker-Wellingborough", "Std", "07:00", "23:00", "00:00", "06:00");

        var collectionWindow = SiteTimingRuleMatcher.CollectionWindow(rule, new DateOnly(2026, 9, 10));
        var deliveryWindow = SiteTimingRuleMatcher.DeliveryWindow(rule, new DateOnly(2026, 9, 10));

        Assert.Equal(new DateTimeOffset(2026, 9, 10, 22, 0, 0, TimeSpan.Zero), collectionWindow.Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 5, 0, 0, TimeSpan.Zero), deliveryWindow.End);
    }

    [Fact]
    public void Dispatch_route_timing_uses_master_collection_start_and_final_delivery_cutoff()
    {
        var collection = new Site { ExternalCode = "NWF-Mer", Name = "NWF - Merston", DriverTextName = "NWF - Merston" };
        var delivery = new Site { ExternalCode = "MOR-LAT", Name = "Morrisons-Lattimer", DriverTextName = "Morrisons-Lattimer" };
        var load = new Load
        {
            Reference = "MERSTON-LATTIMER",
            PlanningDate = new DateOnly(2026, 10, 20),
            Stops =
            [
                new LoadStop { Sequence = 1, Name = "Collect · NWF - Merston" },
                new LoadStop { Sequence = 2, Name = "Deliver · Morrisons-Lattimer" }
            ]
        };
        var rule = new SiteTimingRule("NWF-Mer-Morrisons-Lattimer", "Std", "07:00", "06:00", "07:00", "18:00");

        var collectionTime = DispatchRouteTiming.PlannedCollectionTimeUtc(load, load.Stops[0], [rule], [collection, delivery]);
        var deliveryCutoff = DispatchRouteTiming.FinalDeliveryCutoffTimeUtc(load, load.Stops[1], [rule], [collection, delivery]);

        Assert.Equal(new DateTimeOffset(2026, 10, 20, 5, 0, 0, TimeSpan.Zero), collectionTime);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero), deliveryCutoff);
    }

    [Fact]
    public void Previous_run_finish_uses_the_later_of_planned_finish_and_master_deadline()
    {
        var collection = new Site { ExternalCode = "NWF-Mer", Name = "NWF - Merston" };
        var delivery = new Site { ExternalCode = "MOR07", Name = "Morrisons-Wakefield" };
        var run = new Load
        {
            Reference = "PREVIOUS-01",
            PlanningDate = new DateOnly(2026, 9, 10),
            Stops =
            [
                new LoadStop { Sequence = 1, Name = "Collect · NWF - Merston", PlannedArrivalUtc = DateTimeOffset.Parse("2026-09-10T04:00:00Z") },
                new LoadStop { Sequence = 2, Name = "Deliver · Morrisons-Wakefield", PlannedArrivalUtc = DateTimeOffset.Parse("2026-09-10T16:00:00Z") }
            ]
        };
        var rule = new SiteTimingRule("NWF-Mer-Morrisons-Wakefield", "Std", null, null, null, "18:00");

        var finish = PlannerStartTimingRules.PreviousRunFinish(run, [rule], [collection, delivery]);

        Assert.Equal(DateTimeOffset.Parse("2026-09-10T17:00:00Z"), finish);
    }

    [Fact]
    public void Previous_run_finish_does_not_replace_a_later_planned_finish()
    {
        var collection = new Site { ExternalCode = "NWF-Mer", Name = "NWF - Merston" };
        var delivery = new Site { ExternalCode = "MOR07", Name = "Morrisons-Wakefield" };
        var run = new Load
        {
            Reference = "PREVIOUS-02",
            PlanningDate = new DateOnly(2026, 9, 10),
            Stops =
            [
                new LoadStop { Sequence = 1, Name = "Collect · NWF - Merston", PlannedArrivalUtc = DateTimeOffset.Parse("2026-09-10T04:00:00Z") },
                new LoadStop { Sequence = 2, Name = "Deliver · Morrisons-Wakefield", PlannedArrivalUtc = DateTimeOffset.Parse("2026-09-10T20:00:00Z") }
            ]
        };
        var rule = new SiteTimingRule("NWF-Mer-Morrisons-Wakefield", "Std", null, null, null, "18:00");

        var finish = PlannerStartTimingRules.PreviousRunFinish(run, [rule], [collection, delivery]);

        Assert.Equal(DateTimeOffset.Parse("2026-09-10T20:00:00Z"), finish);
    }
}
