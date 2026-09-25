using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class StartupConfigurationValidatorTests
{
    [Fact]
    public void Production_rejects_missing_core_configuration()
    {
        var configuration = new ConfigurationBuilder().Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            StartupConfigurationValidator.Validate(configuration, Environment("Production")));

        Assert.Contains("Entra:TenantId", error.Message);
        Assert.Contains("Entra:Audience", error.Message);
        Assert.Contains("ConnectionStrings:TmsDb", error.Message);
    }

    [Fact]
    public void Production_accepts_minimum_valid_configuration()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Entra:TenantId"] = "5aec48a1-c3c7-4cfd-a073-b38ae50041b1",
            ["Entra:Audience"] = "api://497f6ea5-9753-43ee-8ccf-afaa0a3869c2",
            ["ConnectionStrings:TmsDb"] = "Server=localhost;Database=SLH_TMS_V2;User Id=sa;Password=TestOnly123!;TrustServerCertificate=True",
            ["Database:ExpectedDatabaseName"] = "SLH_TMS_V2"
        });

        StartupConfigurationValidator.Validate(configuration, Environment("Production"));
    }

    [Fact]
    public void Enabled_integration_requires_its_credentials()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Entra:TenantId"] = "5aec48a1-c3c7-4cfd-a073-b38ae50041b1",
            ["Entra:Audience"] = "api://497f6ea5-9753-43ee-8ccf-afaa0a3869c2",
            ["ConnectionStrings:TmsDb"] = "Server=localhost;Database=SLH_TMS_V2;User Id=sa;Password=TestOnly123!;TrustServerCertificate=True",
            ["Integrations:Samsara:Enabled"] = "true",
            ["Integrations:Samsara:BaseUrl"] = "https://api.samsara.com"
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
            StartupConfigurationValidator.Validate(configuration, Environment("Production")));

        Assert.Contains("Samsara API token", error.Message);
    }

    [Fact]
    public void Testing_environment_skips_production_runtime_requirements()
    {
        StartupConfigurationValidator.Validate(new ConfigurationBuilder().Build(), Environment("Testing"));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHostEnvironment Environment(string name) => new TestHostEnvironment
    {
        EnvironmentName = name,
        ApplicationName = "Slh.Tms.Api.Tests",
        ContentRootPath = Directory.GetCurrentDirectory(),
        ContentRootFileProvider = new NullFileProvider()
    };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
