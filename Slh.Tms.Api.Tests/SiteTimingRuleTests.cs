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
}
