using System.Reflection;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoDriverMasterIdentityTests
{
    [Theory]
    [InlineData("14/03/2026", true)]
    [InlineData("13/03/2026", false)]
    [InlineData("14/09/2026", true)]
    [InlineData("15/09/2026", false)]
    [InlineData("", false)]
    [InlineData("not a date", false)]
    public void Card_read_eligibility_is_limited_to_the_inclusive_six_month_window(string lastRead, bool expected)
    {
        Assert.Equal(expected, TachoDriverCardReadEligibility.IsEligible(lastRead, new DateOnly(2026, 9, 14)));
    }

    [Fact]
    public void Card_read_cutoff_uses_the_UK_calendar_date()
    {
        Assert.Equal(new DateOnly(2026, 9, 15), TachoDriverCardReadEligibility.UkToday(new DateTimeOffset(2026, 9, 14, 23, 30, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("14/03/2026", true)]
    [InlineData("13/03/2026", false)]
    [InlineData("14/09/2026", true)]
    [InlineData("15/09/2026", false)]
    [InlineData("", false)]
    [InlineData("not a date", false)]
    public void Card_read_eligibility_includes_only_the_last_six_months_and_never_future_reads(string lastRead, bool expected)
    {
        Assert.Equal(expected, TachoDriverCardReadEligibility.IsEligible(lastRead, new DateOnly(2026, 9, 14)));
    }

    [Fact]
    public void Member_code_is_a_unique_secondary_identity()
    {
        Assert.True(TachoDriverIdentityRules.MemberMatches(" 1955725 ", "1955725"));
        Assert.False(TachoDriverIdentityRules.MemberMatches("1631289", "1955725"));
    }

    [Fact]
    public void Card_identity_tolerates_provider_prefix_or_suffix_formatting()
    {
        Assert.True(TachoDriverIdentityRules.CardsMatch("V100000149273000", "V100000149273000"));
        Assert.True(TachoDriverIdentityRules.CardsMatch("GB-V100000149273000", "V100000149273000"));
        Assert.False(TachoDriverIdentityRules.CardsMatch("V100000149273000", "V100000325782000"));
    }

    [Fact]
    public void Same_name_does_not_make_different_member_codes_the_same_driver()
    {
        Assert.Equal(
            TachoDriverIdentityRules.NormalisePerson("Gerika, Donatas"),
            TachoDriverIdentityRules.NormalisePerson("Donatas Gerika"));
        Assert.False(TachoDriverIdentityRules.MemberMatches("1385435", "2056313"));
    }

    [Fact]
    public void Missing_card_does_not_override_a_valid_member_identity()
    {
        Assert.True(TachoDriverIdentityRules.MemberMatches("1955729", "1955729"));
        Assert.False(TachoDriverIdentityRules.CardsMatch(null, null));
    }

    [Fact]
    public void Duplicate_live_card_is_not_a_safe_identity_key()
    {
        var card = "CARD-DUPLICATE-0001";
        var workers = new[]
        {
            new TachoLiveWorker(1, "Driver One", card, "SLH-1", "Employed", null, null, null, null, null, null, null, null, null, null, null, "{}"),
            new TachoLiveWorker(2, "Driver Two", card, "SLH-2", "Employed", null, null, null, null, null, null, null, null, null, null, null, "{}")
        };

        var cardKey = TachoDriverIdentityRules.NormaliseIdentifier(card);
        var liveCardCounts = workers
            .Where(worker => !string.IsNullOrWhiteSpace(worker.CardNumber))
            .GroupBy(worker => TachoDriverIdentityRules.NormaliseIdentifier(worker.CardNumber), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, liveCardCounts[cardKey]);
        Assert.False(cardKey.Length > 0 && liveCardCounts.GetValueOrDefault(cardKey) == 1);
    }

    [Fact]
    public void Live_worker_directory_is_collapsed_to_one_driver_per_physical_card()
    {
        var card = "GB-V100000149273000";
        var loadedDriver = new Driver
        {
            Id = Guid.NewGuid(),
            EmployeeNumber = "SLH-42",
            DisplayName = "Driver One",
            TachoMasterDriverId = "42",
            TachoCardNumber = "V100000149273000",
            Active = true
        };
        var workers = new[]
        {
            new TachoLiveWorker(99, "One, Driver", card, "TM-99", "Employed", null, null, null, null, null, null, null, null, null, null, null, "{}"),
            new TachoLiveWorker(42, "Driver One", "V100000149273000", "SLH-42", "Employed", null, null, null, null, null, null, null, null, null, null, null, "{}")
        };

        var canonical = TachoDriverMasterSyncService.CanonicaliseLiveWorkers(
            workers,
            [loadedDriver],
            new Dictionary<Guid, int> { [loadedDriver.Id] = 12 });

        var selected = Assert.Single(canonical);
        Assert.Equal(42, selected.MemberCode);
        Assert.Equal("SLH-42", selected.EmployeeNumber);
    }

    [Fact]
    public void Cardless_alias_for_an_existing_card_member_is_not_a_second_driver()
    {
        var workers = new[]
        {
            new TachoLiveWorker(42, "Driver One", "CARD42000000", "SLH-42", "Employed", null, null, null, null, null, null, null, null, null, null, null, "{}"),
            new TachoLiveWorker(42, "Driver One", null, "SLH-42", "Employed", null, null, null, null, null, null, null, null, null, null, null, "{}")
        };

        var canonical = TachoDriverMasterSyncService.CanonicaliseLiveWorkers(
            workers,
            [],
            new Dictionary<Guid, int>());

        Assert.Single(canonical);
        Assert.Equal("CARD42000000", canonical[0].CardNumber);
    }

    [Fact]
    public void Missing_profile_metrics_preserve_last_known_tacho_hours()
    {
        var driver = new Driver
        {
            EmployeeNumber = "SLH-42",
            DisplayName = "Test Driver",
            TachoDriveAvailableTodayMinutes = 360,
            TachoDriveAvailableWeekMinutes = 1440,
            TachoWorkAvailableWeekMinutes = 2100
        };
        var worker = new TachoLiveWorker(
            42,
            "Test Driver",
            "CARD42000000",
            "SLH-42",
            "Employed",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "{}");

        var applyWorker = typeof(TachoDriverMasterSyncService).GetMethod(
            "ApplyWorker",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(applyWorker);
        applyWorker!.Invoke(null, [driver, worker, null, DateTimeOffset.UtcNow]);

        Assert.Equal(360, driver.TachoDriveAvailableTodayMinutes);
        Assert.Equal(1440, driver.TachoDriveAvailableWeekMinutes);
        Assert.Equal(2100, driver.TachoWorkAvailableWeekMinutes);
    }
}
