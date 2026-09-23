using System.Net.Http.Headers;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Tests;

public class CustomWebFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"slh-tms-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TvWallboard:AccessKey"] = "test-tv-wallboard-key-20260824"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            var dbRegistrations = services.Where(descriptor =>
                descriptor.ServiceType == typeof(TmsDbContext) ||
                descriptor.ServiceType == typeof(DbContextOptions<TmsDbContext>) ||
                descriptor.ServiceType == typeof(DbContextOptions) ||
                (descriptor.ServiceType.IsGenericType &&
                 descriptor.ServiceType.GetGenericTypeDefinition().FullName == "Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration`1" &&
                 descriptor.ServiceType.GenericTypeArguments.Contains(typeof(TmsDbContext)))).ToList();
            foreach (var registration in dbRegistrations) services.Remove(registration);
            services.AddDbContext<TmsDbContext>(options => options.UseInMemoryDatabase(_databaseName));

            // Application Insights 3.x uses one process-wide configuration. Tests use the
            // assembly-initialised disabled client so the production factory never mutates that
            // already-built configuration and no telemetry leaves the test process.
            var telemetryRegistrations = services.Where(descriptor => descriptor.ServiceType == typeof(TelemetryClient)).ToList();
            foreach (var registration in telemetryRegistrations) services.Remove(registration);
            services.AddSingleton(TestTelemetry.Client);

            // Operational intelligence workers are production schedulers, not endpoint dependencies.
            // Starting them inside WebApplicationFactory makes them race the shared in-memory provider
            // and can stop/dispose the test host while unrelated endpoint tests are still running.
            var operationalWorkers = services.Where(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType is Type implementation &&
                (implementation == typeof(BackloadTriggerHostedService) ||
                 implementation == typeof(LiveEtaService) ||
                 implementation == typeof(EtaAccuracyService))).ToList();
            foreach (var registration in operationalWorkers) services.Remove(registration);

            // Replace authentication with test scheme
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    public HttpClient CreateClientWithUser(string userName, string scopes = "")
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User", userName);
        if (!string.IsNullOrEmpty(scopes)) c.DefaultRequestHeaders.Add("X-Test-Scopes", scopes);
        return c;
    }
}
