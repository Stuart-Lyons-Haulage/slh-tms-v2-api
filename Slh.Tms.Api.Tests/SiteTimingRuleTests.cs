using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

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
    public void Window_uses_UK_local_time_and_supports_after_midnight_deadline()
    {
        var rule = new SiteTimingRule("NWF-Mer-Booker-Wellingborough", "Std", "07:00", "23:00", "00:00", "06:00");

        var collectionWindow = SiteTimingRuleMatcher.CollectionWindow(rule, new DateOnly(2026, 9, 10));
        var deliveryWindow = SiteTimingRuleMatcher.DeliveryWindow(rule, new DateOnly(2026, 9, 10));

        Assert.Equal(new DateTimeOffset(2026, 9, 10, 22, 0, 0, TimeSpan.Zero), collectionWindow.Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 5, 0, 0, TimeSpan.Zero), deliveryWindow.End);
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
