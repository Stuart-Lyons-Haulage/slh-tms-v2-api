using System.Runtime.CompilerServices;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;

namespace Slh.Tms.Api.Tests;

internal static class TestTelemetry
{
    private static TelemetryClient? client;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Application Insights 3.x uses a process-wide default configuration. Build it once,
        // before any WebApplicationFactory starts, with telemetry disabled so unit/integration
        // tests never need an ingestion endpoint and never mutate a built configuration later.
        const string variable = "APPLICATIONINSIGHTS_CONNECTION_STRING";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(
                variable,
                "InstrumentationKey=11111111-1111-1111-1111-111111111111;IngestionEndpoint=http://127.0.0.1:1/");
            var configuration = TelemetryConfiguration.CreateDefault();
            configuration.DisableTelemetry = true;
            client = new TelemetryClient(configuration);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    internal static TelemetryClient Client => client ?? throw new InvalidOperationException("Test telemetry was not initialised.");
}
