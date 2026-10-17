using Microsoft.Data.SqlClient;

namespace Slh.Tms.Api.Services;

public static class StartupConfigurationValidator
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        // WebApplicationFactory replaces production services after Program starts.
        // Keep its isolated in-memory Testing environment free from production-only
        // SQL and Entra requirements while validating every real runtime.
        if (environment.IsEnvironment("Testing")) return;

        var errors = new List<string>();

        Require(configuration, errors, "Entra:TenantId", "Microsoft Entra tenant ID");
        Require(configuration, errors, "Entra:Audience", "Microsoft Entra API audience");

        var connectionString = configuration.GetConnectionString("TmsDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            errors.Add("ConnectionStrings:TmsDb is required.");
        }
        else
        {
            try
            {
                var sql = new SqlConnectionStringBuilder(connectionString);
                if (string.IsNullOrWhiteSpace(sql.DataSource))
                    errors.Add("ConnectionStrings:TmsDb must specify a SQL Server.");
                if (string.IsNullOrWhiteSpace(sql.InitialCatalog))
                    errors.Add("ConnectionStrings:TmsDb must specify a database.");
                var expectedDatabase = Value(configuration, "Database:ExpectedDatabaseName");
                if (!string.IsNullOrWhiteSpace(expectedDatabase) &&
                    !string.Equals(sql.InitialCatalog, expectedDatabase, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"ConnectionStrings:TmsDb targets '{sql.InitialCatalog}' but Database:ExpectedDatabaseName is '{expectedDatabase}'.");
            }
            catch (ArgumentException ex)
            {
                errors.Add($"ConnectionStrings:TmsDb is invalid: {ex.Message}");
            }
        }

        ValidateEnabledIntegrations(configuration, errors);

        if (errors.Count > 0)
            throw new InvalidOperationException(
                "SLH TMS startup configuration is invalid:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(error => $" - {error}")));
    }

    private static void ValidateEnabledIntegrations(IConfiguration configuration, List<string> errors)
    {
        if (Enabled(configuration, "Tracking:Dot:Enabled", "dot-enabled"))
        {
            RequireUrl(configuration, errors, "Tracking:Dot:BaseUrl", "RoadTech base URL");
            Require(configuration, errors, "Tracking:Dot:ApiKey", "RoadTech API key");
            Require(configuration, errors, "Tracking:Dot:Username", "RoadTech username");
            Require(configuration, errors, "Tracking:Dot:Password", "RoadTech password");
            Require(configuration, errors, "Tracking:Dot:CompanyCode", "RoadTech company code");
        }

        if (Enabled(configuration, "Integrations:TachoMaster:Enabled", "tachomaster-enabled", "tacho-enabled"))
        {
            RequireUrl(configuration, errors, "Integrations:TachoMaster:BaseUrl", "TachoMaster base URL");

            var hasDedicatedCredentials =
                HasValue(configuration, "Integrations:TachoMaster:ApiKey", "tachomaster-api-key", "tacho-api-key") &&
                HasValue(configuration, "Integrations:TachoMaster:Username", "tachomaster-username", "tacho-username") &&
                HasValue(configuration, "Integrations:TachoMaster:Password", "tachomaster-password", "tacho-password");
            var hasSharedRoadTechCredentials =
                HasValue(configuration, "Tracking:Dot:ApiKey", "dot-api-key") &&
                HasValue(configuration, "Tracking:Dot:Username", "dot-username") &&
                HasValue(configuration, "Tracking:Dot:Password", "dot-password");

            if (!hasDedicatedCredentials && !hasSharedRoadTechCredentials)
                errors.Add("TachoMaster is enabled but neither dedicated credentials nor complete RoadTech shared credentials are configured.");
        }

        if (Enabled(configuration, "Integrations:SageHr:Enabled"))
        {
            RequireUrl(configuration, errors, "Integrations:SageHr:BaseUrl", "Sage HR base URL");
            Require(configuration, errors, "Integrations:SageHr:ApiKey", "Sage HR API key");
        }

        if (Enabled(configuration, "Integrations:Fleetio:Enabled", "fleetio-enabled"))
        {
            RequireUrl(configuration, errors, "Integrations:Fleetio:BaseUrl", "Fleetio base URL");
            Require(configuration, errors, "Integrations:Fleetio:ApiKey", "Fleetio API key");
            Require(configuration, errors, "Integrations:Fleetio:AccountToken", "Fleetio account token");
        }

        if (Enabled(configuration, "Integrations:Samsara:Enabled"))
        {
            RequireUrl(configuration, errors, "Integrations:Samsara:BaseUrl", "Samsara base URL");
            Require(configuration, errors, "Integrations:Samsara:ApiToken", "Samsara API token");
        }

        if (Enabled(configuration, "Integrations:InfoMailboxGraph:Enabled"))
        {
            Require(configuration, errors, "Integrations:InfoMailboxGraph:TenantId", "Info mailbox Graph tenant ID");
            Require(configuration, errors, "Integrations:InfoMailboxGraph:ClientId", "Info mailbox Graph client ID");
            Require(configuration, errors, "Integrations:InfoMailboxGraph:ClientSecret", "Info mailbox Graph client secret");
            Require(configuration, errors, "Integrations:InfoMailboxGraph:Mailbox", "Info mailbox address");
        }

        if (Enabled(configuration, "Integrations:OpenAI:Enabled", "openai-enabled"))
        {
            RequireUrl(configuration, errors, "Integrations:OpenAI:BaseUrl", "OpenAI base URL");
            Require(configuration, errors, "Integrations:OpenAI:ApiKey", "OpenAI API key");
        }

        if (Enabled(configuration, "Integrations:TextBee:Enabled"))
        {
            RequireUrl(configuration, errors, "Integrations:TextBee:BaseUrl", "TextBee base URL");
            Require(configuration, errors, "Integrations:TextBee:ApiKey", "TextBee API key");
            Require(configuration, errors, "Integrations:TextBee:DeviceId", "TextBee device ID");
        }

        if (Enabled(configuration, "Integrations:AzureSms:Enabled"))
        {
            Require(configuration, errors, "Integrations:AzureSms:ConnectionString", "Azure SMS connection string");
            Require(configuration, errors, "Integrations:AzureSms:From", "Azure SMS sender");
        }

        if (Enabled(configuration, "Archive:Enabled"))
            Require(configuration, errors, "Archive:RootPath", "archive root path");
    }

    private static void Require(IConfiguration configuration, List<string> errors, string key, string label)
    {
        if (!HasValue(configuration, key))
            errors.Add($"{label} ({key}) is required.");
    }

    private static void RequireUrl(IConfiguration configuration, List<string> errors, string key, string label)
    {
        var value = Value(configuration, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{label} ({key}) is required.");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            errors.Add($"{label} ({key}) must be an absolute HTTP(S) URL.");
    }

    private static bool Enabled(IConfiguration configuration, params string[] keys) =>
        bool.TryParse(Value(configuration, keys), out var enabled) && enabled;

    private static bool HasValue(IConfiguration configuration, params string[] keys) =>
        !string.IsNullOrWhiteSpace(Value(configuration, keys));

    private static string? Value(IConfiguration configuration, params string[] keys) =>
        keys.Select(key => configuration[key]).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
