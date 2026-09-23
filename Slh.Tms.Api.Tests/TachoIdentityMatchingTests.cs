using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoIdentityMatchingTests
{
    [Fact]
    public void Driver_match_prefers_tacho_card_when_names_and_employee_numbers_differ()
    {
        var driver = new Driver
        {
            EmployeeNumber = "SLH-100",
            DisplayName = "Stuart Lyons",
            TachoName = "S Lyons",
            TachoCardNumber = "UK-DRIVER-123456"
        };
        var tacho = Status(memberCode: 42, driverName: "Different Provider Name", cardNumber: "UK DRIVER 123456", employeeNumber: "OTHER-999");

        Assert.True(ExecutionIdentityResolver.DriverMatches(driver, tacho));
    }

    [Fact]
    public void Driver_match_uses_tachomaster_member_code_when_card_is_unavailable()
    {
        var driver = new Driver
        {
            EmployeeNumber = "SLH-100",
            DisplayName = "Stuart Lyons",
            TachoMasterDriverId = "42"
        };
        var tacho = Status(memberCode: 42, driverName: "Different Provider Name", cardNumber: null, employeeNumber: "OTHER-999");

        Assert.True(ExecutionIdentityResolver.DriverMatches(driver, tacho));
    }

    [Fact]
    public void Driver_match_fails_closed_when_all_strong_and_fallback_identities_differ()
    {
        var driver = new Driver
        {
            EmployeeNumber = "SLH-100",
            DisplayName = "Stuart Lyons",
            TachoName = "S Lyons",
            TachoCardNumber = "UK-DRIVER-123456",
            TachoMasterDriverId = "42"
        };
        var tacho = Status(memberCode: 99, driverName: "Another Driver", cardNumber: "UK-DRIVER-999999", employeeNumber: "OTHER-999");

        Assert.False(ExecutionIdentityResolver.DriverMatches(driver, tacho));
    }

    private static TachoVehicleDriverStatus Status(int memberCode, string driverName, string? cardNumber, string? employeeNumber)
        => new(
            "AB12CDE",
            memberCode,
            driverName,
            cardNumber,
            employeeNumber,
            new DateTimeOffset(2026, 8, 21, 6, 0, 0, TimeSpan.Zero),
            null,
            60,
            0,
            0,
            30,
            0,
            null,
            DateTimeOffset.UtcNow,
            1,
            480,
            540,
            1800,
            4000,
            0,
            0,
            2000);
}
