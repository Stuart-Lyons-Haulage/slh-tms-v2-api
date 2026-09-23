using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoDriverMasterCardAliasRegressionTests
{
    [Fact]
    public void Canonicalise_live_workers_prefers_newest_card_read_when_tachomaster_returns_card_aliases()
    {
        var workers = new[]
        {
            new TachoLiveWorker(1001, "Driver Alias Old", "GB-V100000149273000", "TM-1001", "Employed", null, null, null, "01/04/2026", null, null, null, null, null, null, null, "{}"),
            new TachoLiveWorker(1002, "Driver Alias Current", "V100000149273000", "SLH-1002", "Employed", null, null, null, "15/09/2026", null, null, null, null, null, null, null, "{}")
        };

        var canonical = TachoDriverMasterSyncService.CanonicaliseLiveWorkers(
            workers,
            [],
            new Dictionary<Guid, int>());

        var selected = Assert.Single(canonical);
        Assert.Equal(1002, selected.MemberCode);
        Assert.Equal("SLH-1002", selected.EmployeeNumber);
        Assert.Equal("15/09/2026", selected.CardLastRead);
    }

    [Fact]
    public void Canonicalise_live_workers_does_not_keep_a_cardless_member_alias_when_a_carded_row_exists()
    {
        var workers = new[]
        {
            new TachoLiveWorker(2001, "Driver Alias", null, "TM-2001", "Employed", null, null, null, "15/09/2026", null, null, null, null, null, null, null, "{}"),
            new TachoLiveWorker(2001, "Driver Alias", "V100000325782000", "SLH-2001", "Employed", null, null, null, "14/09/2026", null, null, null, null, null, null, null, "{}")
        };

        var canonical = TachoDriverMasterSyncService.CanonicaliseLiveWorkers(
            workers,
            [],
            new Dictionary<Guid, int>());

        var selected = Assert.Single(canonical);
        Assert.Equal("V100000325782000", selected.CardNumber);
        Assert.Equal("SLH-2001", selected.EmployeeNumber);
    }

    [Fact]
    public void Card_match_lookup_must_not_depend_on_live_card_uniqueness()
    {
        var card = "V100000149273000";
        var drivers = new[]
        {
            new Driver
            {
                Id = Guid.NewGuid(),
                EmployeeNumber = "SLH-1002",
                DisplayName = "Driver Alias Current",
                TachoCardNumber = card,
                TachoMasterDriverId = "1002",
                Active = true
            }
        };
        var workers = new[]
        {
            new TachoLiveWorker(1001, "Driver Alias Old", "GB-V100000149273000", "TM-1001", "Employed", null, null, null, "01/04/2026", null, null, null, null, null, null, null, "{}"),
            new TachoLiveWorker(1002, "Driver Alias Current", card, "SLH-1002", "Employed", null, null, null, "15/09/2026", null, null, null, null, null, null, null, "{}")
        };

        var duplicateLiveCardCount = workers.Count(worker => TachoDriverIdentityRules.CardsMatch(worker.CardNumber, card));
        var cardMatches = drivers
            .Where(driver => TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, workers[0].CardNumber))
            .ToList();

        Assert.Equal(2, duplicateLiveCardCount);
        var matched = Assert.Single(cardMatches);
        Assert.Equal("SLH-1002", matched.EmployeeNumber);
    }
}
