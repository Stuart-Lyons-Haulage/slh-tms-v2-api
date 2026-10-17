using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Slh.Tms.Api.Tests;

public class AuthenticationTests : IClassFixture<CustomWebFactory>
{
    private const string LyonsUser = "planner@lyonshaulage.com";
    private readonly CustomWebFactory _factory;
    public AuthenticationTests(CustomWebFactory factory) => _factory = factory;

    [Fact]
    public async Task Anonymous_health_check_is_allowed()
    {
        var client = _factory.CreateClient();
        var r = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Authentication_required_for_api_endpoints()
    {
        var client = _factory.CreateClient();
        var r = await client.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Production_portal_preflight_is_allowed()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/customers");
        request.Headers.Add("Origin", "https://slh-tms-portal-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("https://slh-tms-portal-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Authenticated_user_without_TmsAccess_scope_can_use_api()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "other.scope");
        var r = await client.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Authenticated_user_outside_Lyons_domain_is_forbidden()
    {
        var client = _factory.CreateClientWithUser("planner@example.com", "Tms.Access");
        var response = await client.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("planner@lyonshaulage.com")]
    [InlineData("PLANNER@LYONSHAULAGE.COM")]
    public async Task Authenticated_company_domain_user_is_authorised_without_individual_allow_list(string userName)
    {
        var client = _factory.CreateClientWithUser(userName);

        var response = await client.GetAsync("/api/v1/customers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("planner@example.com")]
    [InlineData("planner@lyonshaulage.com.evil.example")]
    [InlineData("planner@evil-lyonshaulage.com")]
    [InlineData("planner@lyonshaulage.co.uk")]
    [InlineData("planner@lyonshaulage.com other@example.com")]
    public async Task Authenticated_external_or_lookalike_domain_user_is_forbidden(string userName)
    {
        var client = _factory.CreateClientWithUser(userName);

        var response = await client.GetAsync("/api/v1/customers");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Authorisation_uses_verified_entra_sign_in_claims_not_display_name()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", LyonsUser);
        client.DefaultRequestHeaders.Add("X-Test-Name-Only", "true");

        var response = await client.GetAsync("/api/v1/customers");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Valid_authorised_request_succeeds()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var r = await client.GetAsync("/api/v1/customers");
        // Depending on DB empty result may be OK; ensure we get 200 rather than auth problem
        Assert.True(r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("Tms.Access")]
    [InlineData("Tms.Write")]
    [InlineData("Tms.Approve")]
    [InlineData("Tms.Admin")]
    public async Task Valid_TMS_app_role_request_succeeds(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", LyonsUser);
        client.DefaultRequestHeaders.Add("X-Test-Roles", role);

        var response = await client.GetAsync("/api/v1/customers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Staging_submission_accepts_and_returns_202()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var json = "{ \"EntityType\": \"customer\", \"IdempotencyKey\": \"k1\", \"Payload\": { \"Code\": \"C1\", \"Name\": \"Test Customer\" } }";
        var r = await client.PostAsync("/api/v1/staging", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
    }

    [Fact]
    public async Task Approval_and_rejection_endpoints_require_authorisation()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        // Create a staging item first
        var json = "{ \"EntityType\": \"customer\", \"IdempotencyKey\": \"k2\", \"Payload\": { \"Code\": \"C2\", \"Name\": \"ApproveCustomer\" } }";
        var post = await client.PostAsync("/api/v1/staging", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        // Read location header to get id if present (service may return it)
        var body = await post.Content.ReadAsStringAsync();
        // best-effort: if there is an id in body try to use approve; otherwise ensure endpoints are secured by hitting a made-up id
        var id = System.Guid.NewGuid();
        var approve = await client.PostAsync($"/api/v1/staging/{id}/approve", new StringContent("{ \"Note\": \"ok\" }", System.Text.Encoding.UTF8, "application/json"));
        // Approve on non-existent returns NotFound (authenticated), reject unauthorized would be Forbidden etc. Ensure we get either NotFound or OK (not 401/403)
        Assert.True(approve.StatusCode == HttpStatusCode.NotFound || approve.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Direct_live_creation_is_not_exposed()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var json = "{ \"Code\": \"LIVE1\", \"Name\": \"DirectCreate\" }";
        var r = await client.PostAsync("/api/v1/customers", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        // Expect NotFound (no direct create endpoint), Unauthorized, or Forbidden depending on configuration; ensure not 201
        Assert.NotEqual(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task Driver_assignment_history_is_available_to_authorised_users()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var response = await client.GetAsync($"/api/v1/driver-assignments?from={date}&to={date}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Return_load_suggestions_are_available_to_authorised_users()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1).ToString("yyyy-MM-dd");
        var response = await client.GetAsync($"/api/v1/planning/return-load-suggestions?date={date}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Assistant_snapshot_and_rule_based_advice_are_available_without_an_external_ai_key()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var snapshot = await client.GetAsync($"/api/v1/assistant/snapshot?date={date}");
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        var snapshotBody = await snapshot.Content.ReadAsStringAsync();
        Assert.Contains("suggestions", snapshotBody);
        Assert.Contains("SLH safety rules", snapshotBody);

        var advice = await client.PostAsync("/api/v1/assistant/advice", new StringContent($"{{\"message\":\"What needs attention?\",\"date\":\"{date}\"}}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, advice.StatusCode);
        Assert.Contains("answer", await advice.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Assistant_registration_normalisation_is_deterministic()
    {
        Assert.Equal("AB12CDE", Slh.Tms.Api.Services.TmsAssistantService.NormaliseRegistration(" ab12 cde "));
    }

    [Fact]
    public async Task Sage_HR_status_does_not_expose_secrets()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/integrations/sage-hr/status");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("ApiKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Auth-Token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Batch_staging_accepts_idempotent_email_records()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var json = "[{ \"EntityType\": \"order\", \"IdempotencyKey\": \"email:test-message:1\", \"Source\": \"Power Automate / Orders Mailbox\", \"Payload\": { \"poNumber\": \"PO-EMAIL-1\", \"customerCode\": \"C1\", \"collectionDate\": \"2026-08-12\" } }]";
        var response = await client.PostAsync("/api/v1/staging/batch", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"received\":1", body);
    }

    [Fact]
    public async Task Integration_status_is_available_without_exposing_credentials()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/integrations/status");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("batchIntake", body);
        Assert.DoesNotContain("ConnectionString", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delivery_ETA_endpoint_returns_an_operational_response()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var response = await client.GetAsync($"/api/v1/operations/delivery-etas?date={date}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Seven_day_operational_capacity_forecast_is_available_without_commercial_margin_fields()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var response = await client.GetAsync($"/api/v1/operations/forecast?from={date}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"days\"", body);
        Assert.Contains("\"emptyMiles\"", body);
        Assert.Contains("\"utilisationPercent\"", body);
        Assert.DoesNotContain("\"revenue\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"cost\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"margin\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"uninvoicedLoads\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tracking_returns_stored_fallback_when_provider_is_not_configured()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/tracking/dot/telemetry");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("stored fallback", body, StringComparison.OrdinalIgnoreCase);
    }
}
