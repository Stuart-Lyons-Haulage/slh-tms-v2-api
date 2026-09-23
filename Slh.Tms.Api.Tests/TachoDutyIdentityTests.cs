using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoDutyIdentityTests
{
    [Fact]
    public void Driver_specific_match_keeps_real_duty_when_remaining_hours_metric_is_missing()
    {
        var driver = new Driver
        {
            EmployeeNumber = "1234",
            DisplayName = "Joe Bloggs",
            TachoName = "Joe Bloggs"
        };
        var duty = Duty("AB12CDE", 77, "Joe Bloggs", "UK12345678901234", "1234", null);
        var statuses = new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AB12CDE"] = new List<TachoVehicleDriverStatus> { duty }
        };

        var match = ExecutionIdentityResolver.MatchTachoForDriver(
            ExecutionIdentityResolver.VehicleAliasVariants("AB12 CDE"), driver, statuses);

        Assert.Same(duty, match);
        var assessment = OperationsController.TachoAssessment(match, 90, 0);
        Assert.Equal("DutyMatchedHoursUnavailable", assessment.Status);
    }

    [Fact]
    public void Driver_card_match_accepts_short_and_full_card_variants()
    {
        var driver = new Driver
        {
            EmployeeNumber = "9999",
            DisplayName = "Different Planner Name",
            TachoCardNumber = "12345678901234"
        };
        var duty = Duty("AB12CDE", 88, "Tacho Name", "UK0012345678901234", "7777", 240);

        Assert.True(ExecutionIdentityResolver.DriverMatches(driver, duty));
    }

    [Fact]
    public void Falcon_identity_only_row_is_not_accepted_as_a_tachomaster_duty()
    {
        var driver = new Driver { EmployeeNumber = "1234", DisplayName = "Joe Bloggs" };
        var falconOnly = Duty("AB12CDE", 0, "Joe Bloggs", null, null, null);
        var statuses = new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AB12CDE"] = new List<TachoVehicleDriverStatus> { falconOnly }
        };

        var match = ExecutionIdentityResolver.MatchTachoForDriver(
            ExecutionIdentityResolver.VehicleAliasVariants("AB12CDE"), driver, statuses);

        Assert.Null(match);

        var liveIdentity = ExecutionIdentityResolver.MatchLiveDriverIdentityForVehicle(
            ExecutionIdentityResolver.VehicleAliasVariants("AB12CDE"), driver, statuses);

        Assert.Same(falconOnly, liveIdentity);
    }

    [Fact]
    public void Falcon_card_with_profile_hours_can_support_eta_assessment_without_claiming_duty()
    {
        var driver = new Driver { EmployeeNumber = "1234", DisplayName = "Joe Bloggs" };
        var falconCard = Duty("AB12CDE", 77, "Joe Bloggs", "UK12345678901234", "1234", 300) with { EvidenceSource = "FalconLiveCard" };
        var statuses = new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AB12CDE"] = new List<TachoVehicleDriverStatus> { falconCard }
        };

        var legalDuty = ExecutionIdentityResolver.MatchTachoForDriver(
            ExecutionIdentityResolver.VehicleAliasVariants("AB12CDE"), driver, statuses);
        var liveIdentity = ExecutionIdentityResolver.MatchLiveDriverIdentityForVehicle(
            ExecutionIdentityResolver.VehicleAliasVariants("AB12CDE"), driver, statuses);
        var assessment = OperationsController.TachoAssessment(liveIdentity, 90, 0);

        Assert.Null(legalDuty);
        Assert.Same(falconCard, liveIdentity);
        Assert.Equal("CardConfirmedWithinDriveTime", assessment.Status);
    }

    private static TachoVehicleDriverStatus Duty(
        string vehicle,
        int memberCode,
        string name,
        string? card,
        string? employee,
        int? driveAvailableToday)
        => new(
            vehicle,
            memberCode,
            name,
            card,
            employee,
            DateTimeOffset.UtcNow.AddHours(-4),
            null,
            60,
            0,
            0,
            60,
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
            null);
}
