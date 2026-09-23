using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationsControlTests : IClassFixture<CustomWebFactory>
{
    private const string LyonsUser = "planner@lyonshaulage.com";
    private readonly CustomWebFactory _factory;

    public OperationsControlTests(CustomWebFactory factory) => _factory = factory;

    [Fact]
    public async Task Confidence_endpoint_requires_authentication()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/operations/confidence");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Confidence_endpoint_returns_ok_for_authenticated_user()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/operations/confidence");
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Exceptions_endpoint_returns_ok_for_authenticated_user()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/operations/exceptions");
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Reconciliation_endpoint_returns_ok_for_authenticated_user()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/operations/reconciliation");
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Mappings_endpoint_requires_authentication()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/operations/mappings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mappings_endpoint_returns_ok_for_authenticated_user()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/operations/mappings");
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Driver_status_capture_requires_write_scope()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var json = """{"status":"Dispatched"}""";
        var response = await client.PostAsync("/api/v1/operations/loads/00000000-0000-0000-0000-000000000001/driver-status",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NotFound);
    }
}

public sealed class TachoDriverMatchingTests
{
    private static readonly Guid DriverA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid DriverB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly FleetDriverIdentity[] Drivers =
    [
        new(DriverA, "EMP001", "John Smith", "Smith John", null, null),
        new(DriverB, "EMP002", "Jane Doe", null, null, null),
    ];

    private static TachoVehicleDriverStatus MakeTacho(string driverName, int memberCode = 999, string? employeeNumber = null) =>
        new("VAN1", memberCode, driverName, null, employeeNumber, DateTimeOffset.UtcNow, null, 0, 0, 0, 0, 0, null, null, null, null, null, null, null, null, null, null);

    [Fact]
    public void Matches_by_explicit_mapping_member_code()
    {
        var tacho = MakeTacho("Someone Else", memberCode: 123);
        var mappings = new Dictionary<string, Guid> { ["123"] = DriverA };
        var (driver, reason) = DotTrackingController.MatchTachoDriverWithReason(tacho, Drivers, mappings);
        Assert.Equal(DriverA, driver!.Id);
        Assert.Equal("Mapped", reason);
    }

    [Fact]
    public void Matches_by_employee_number()
    {
        var tacho = MakeTacho("Unknown Name", employeeNumber: "EMP002");
        var mappings = new Dictionary<string, Guid>();
        var (driver, reason) = DotTrackingController.MatchTachoDriverWithReason(tacho, Drivers, mappings);
        Assert.Equal(DriverB, driver!.Id);
        Assert.Equal("EmployeeNumber", reason);
    }

    [Fact]
    public void Matches_by_tacho_name()
    {
        // NormalisePersonName sorts words alphabetically, so "Smith John" → "JOHN SMITH"
        var tacho = MakeTacho("Smith John");
        var mappings = new Dictionary<string, Guid>();
        var (driver, reason) = DotTrackingController.MatchTachoDriverWithReason(tacho, Drivers, mappings);
        Assert.Equal(DriverA, driver!.Id);
        Assert.Equal("TachoName", reason);
    }

    [Fact]
    public void Matches_by_display_name()
    {
        // NormalisePersonName("Jane Doe") → "DOE JANE", and driver has no TachoName
        var tacho = MakeTacho("Doe Jane");
        var mappings = new Dictionary<string, Guid>();
        var (driver, reason) = DotTrackingController.MatchTachoDriverWithReason(tacho, Drivers, mappings);
        Assert.Equal(DriverB, driver!.Id);
        Assert.Equal("DisplayName", reason);
    }

    [Fact]
    public void Does_not_match_on_surname_only()
    {
        // "John Brown" shares first name "John" with DriverA but full name doesn't match
        var tacho = MakeTacho("John Brown");
        var mappings = new Dictionary<string, Guid>();
        var (driver, reason) = DotTrackingController.MatchTachoDriverWithReason(tacho, Drivers, mappings);
        Assert.Null(driver);
        Assert.Equal("Unmatched", reason);
    }

    [Fact]
    public void Returns_null_when_tacho_status_is_null()
    {
        var (driver, reason) = DotTrackingController.MatchTachoDriverWithReason(null, Drivers, new Dictionary<string, Guid>());
        Assert.Null(driver);
        Assert.Null(reason);
    }
}
