using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class LiveDriverEvidenceRulesTests
{
    [Fact]
    public void Fresh_falcon_card_can_feed_eta_before_open_duty_appears()
    {
        var driver = new Driver
        {
            EmployeeNumber = "1234",
            DisplayName = "Joe Bloggs",
            TachoMasterDriverId = "77",
            TachoCardNumber = "UK12345678901234"
        };
        var card = Status("FalconLiveCard", 77, "Joe Bloggs", "UK12345678901234", 300);
        var statuses = ByVehicle(card);

        var evidence = LiveDriverEvidenceRules.Resolve(
            ExecutionIdentityResolver.VehicleAliasVariants("AB12 CDE"), driver, statuses);

        Assert.Same(card, evidence.LiveCard);
        Assert.Null(evidence.TachoDuty);
        Assert.Same(card, evidence.EtaAuthority);
        Assert.Equal("LiveCardOnly", evidence.CardDutyStatus);
        Assert.Equal("CardConfirmedWithinDriveTime", OperationsController.TachoAssessment(evidence.EtaAuthority, 120, 0).Status);
    }

    [Fact]
    public void Falcon_card_and_tacho_duty_mismatch_suppresses_legal_hours()
    {
        var planned = new Driver
        {
            EmployeeNumber = "1234",
            DisplayName = "Joe Bloggs",
            TachoMasterDriverId = "77",
            TachoCardNumber = "UK12345678901234"
        };
        var card = Status("FalconLiveCard", 88, "Jane Driver", "UK99887766554433", 240);
        var duty = Status("TachoMasterDuty", 77, "Joe Bloggs", "UK12345678901234", 300);
        var statuses = ByVehicle(card, duty);

        var evidence = LiveDriverEvidenceRules.Resolve(
            ExecutionIdentityResolver.VehicleAliasVariants("AB12CDE"), planned, statuses);

        Assert.Equal("Mismatch", evidence.CardDutyStatus);
        Assert.Same(card, evidence.LiveCard);
        Assert.Same(duty, evidence.TachoDuty);
        Assert.Null(evidence.EtaAuthority);
        Assert.Same(card, evidence.DisplayIdentity);
    }

    [Fact]
    public void Matching_live_card_and_duty_keep_duty_as_legal_hours_authority()
    {
        var driver = new Driver { EmployeeNumber = "1234", DisplayName = "Joe Bloggs", TachoMasterDriverId = "77" };
        var card = Status("FalconLiveCard", 77, "Joe Bloggs", "UK12345678901234", 300);
        var duty = Status("TachoMasterDuty", 77, "Joe Bloggs", "UK12345678901234", 275) with { DriveMinutes = 250 };

        var evidence = LiveDriverEvidenceRules.Resolve(
            ExecutionIdentityResolver.VehicleAliasVariants("AB12CDE"), driver, ByVehicle(card, duty));

        Assert.Equal("Matched", evidence.CardDutyStatus);
        Assert.Same(card, evidence.DisplayIdentity);
        Assert.Same(duty, evidence.EtaAuthority);
    }

    [Fact]
    public void Open_card_or_duty_spanning_uk_midnight_is_overnight_evidence()
    {
        var day = new DateOnly(2026, 9, 7);
        var start = new DateTimeOffset(2026, 9, 7, 20, 0, 0, TimeSpan.Zero);

        Assert.True(LiveDriverEvidenceRules.SpansUkMidnight(day, start, null));
        Assert.True(LiveDriverEvidenceRules.SpansUkMidnight(day, start, new DateTimeOffset(2026, 9, 8, 2, 0, 0, TimeSpan.Zero)));
        Assert.False(LiveDriverEvidenceRules.SpansUkMidnight(day, start, new DateTimeOffset(2026, 9, 7, 22, 0, 0, TimeSpan.Zero)));
    }

    private static Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> ByVehicle(params TachoVehicleDriverStatus[] statuses)
        => new(StringComparer.OrdinalIgnoreCase) { ["AB12CDE"] = statuses };

    private static TachoVehicleDriverStatus Status(string source, int memberCode, string name, string? card, int? driveAvailableToday)
        => new(
            "AB12CDE",
            memberCode,
            name,
            card,
            "1234",
            DateTimeOffset.UtcNow.AddMinutes(-15),
            null,
            0,
            0,
            0,
            0,
            0,
            null,
            DateTimeOffset.UtcNow,
            null,
            driveAvailableToday,
            null,
            null,
            null,
            null,
            null,
            null,
            source);
}
