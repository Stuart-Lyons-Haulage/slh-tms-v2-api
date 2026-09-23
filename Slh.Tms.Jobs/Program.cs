using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Slh.Tms.Jobs;

var builder = Host.CreateApplicationBuilder(args);
var configuration = builder.Configuration;

var connectionString = configuration.GetConnectionString("TmsDb")
    ?? throw new InvalidOperationException("ConnectionStrings:TmsDb is required.");
builder.Services.AddDbContext<TmsDbContext>(options => options.UseSqlServer(connectionString));

var dot = new DotTrackingOptions();
configuration.GetSection("Tracking:Dot").Bind(dot);
builder.Services.AddSingleton(dot);

var tacho = new TachoMasterOptions();
configuration.GetSection("Integrations:TachoMaster").Bind(tacho);
tacho.Enabled = ReadBool(configuration, tacho.Enabled, "Integrations:TachoMaster:Enabled", "Integrations__TachoMaster__Enabled", "tachomaster-enabled", "tacho-enabled", "TachoMaster--Enabled");
tacho.BaseUrl = ReadSetting(configuration, tacho.BaseUrl, "Integrations:TachoMaster:BaseUrl", "Integrations__TachoMaster__BaseUrl", "tachomaster-base-url", "tacho-base-url", "TachoMaster--BaseUrl");
tacho.ApiKey = ReadSetting(configuration, tacho.ApiKey, "Integrations:TachoMaster:ApiKey", "Integrations__TachoMaster__ApiKey", "tachomaster-api-key", "tacho-api-key", "TachoMaster--ApiKey");
tacho.Username = ReadSetting(configuration, tacho.Username, "Integrations:TachoMaster:Username", "Integrations__TachoMaster__Username", "tachomaster-username", "tacho-username", "TachoMaster--Username");
tacho.Password = ReadSetting(configuration, tacho.Password, "Integrations:TachoMaster:Password", "Integrations__TachoMaster__Password", "tachomaster-password", "tacho-password", "TachoMaster--Password");
if (string.IsNullOrWhiteSpace(tacho.ApiKey) && string.IsNullOrWhiteSpace(tacho.Username) && string.IsNullOrWhiteSpace(tacho.Password) && dot.IsConfigured)
{
    tacho.Enabled = true;
    tacho.BaseUrl = dot.BaseUrl;
    tacho.ApiKey = dot.ApiKey;
    tacho.Username = dot.Username;
    tacho.Password = dot.Password;
    tacho.UsesSharedRoadTechCredentials = true;
}
builder.Services.AddSingleton(tacho);

var sage = new SageHrOptions();
configuration.GetSection("Integrations:SageHr").Bind(sage);
builder.Services.AddSingleton(sage);

var fleetio = new FleetioOptions();
configuration.GetSection("Integrations:Fleetio").Bind(fleetio);
fleetio.Enabled = ReadBool(configuration, fleetio.Enabled, "Integrations:Fleetio:Enabled", "Integrations__Fleetio__Enabled", "fleetio-enabled", "Fleetio--Enabled");
fleetio.BaseUrl = ReadSetting(configuration, fleetio.BaseUrl, "Integrations:Fleetio:BaseUrl", "Integrations__Fleetio__BaseUrl", "fleetio-base-url", "Fleetio--BaseUrl");
fleetio.ApiKey = ReadSetting(configuration, fleetio.ApiKey, "Integrations:Fleetio:ApiKey", "Integrations__Fleetio__ApiKey", "fleetio-api-key", "Fleetio--ApiKey");
fleetio.AccountToken = ReadSetting(configuration, fleetio.AccountToken, "Integrations:Fleetio:AccountToken", "Integrations__Fleetio__AccountToken", "fleetio-account-token", "Fleetio--AccountToken");
fleetio.ApiVersion = ReadSetting(configuration, fleetio.ApiVersion, "Integrations:Fleetio:ApiVersion", "Integrations__Fleetio__ApiVersion", "fleetio-api-version", "Fleetio--ApiVersion");
if (fleetio.BaseUrl.EndsWith("/api/v2", StringComparison.OrdinalIgnoreCase)) fleetio.BaseUrl = fleetio.BaseUrl[..^1] + "1";
builder.Services.AddSingleton(fleetio);

builder.Services.AddTransient<TachoMasterRetryHandler>();
builder.Services.AddHttpClient<DotTrackingClient>();
builder.Services.AddHttpClient<TachoMasterClient>().AddHttpMessageHandler<TachoMasterRetryHandler>();
builder.Services.AddHttpClient<SageHrClient>();
builder.Services.AddHttpClient<FleetioClient>();
builder.Services.AddHttpClient("eta-job");
builder.Services.AddScoped<DistributedLeaseManager>();
builder.Services.AddScoped<IntegrationSyncCoordinator>();
builder.Services.AddScoped<TachoObservedDriverSyncService>();
builder.Services.AddScoped<TachoDriverMasterSyncService>();
builder.Services.AddScoped<DriverMasterClassificationService>();
builder.Services.AddScoped<TachoCanonicalDriverMasterOrchestrator>();
builder.Services.AddScoped<TachoMasterScheduledJob>();
builder.Services.AddScoped<SageHrScheduledJob>();
builder.Services.AddScoped<EtaRecalculationJob>();
builder.Services.AddScoped<ScheduledJobRunner>();

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var services = scope.ServiceProvider;
var runner = services.GetRequiredService<ScheduledJobRunner>();
var jobKind = (configuration["TMS_JOB_KIND"] ?? args.FirstOrDefault() ?? string.Empty).Trim().ToLowerInvariant();
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };

await EnsureStagedImportTriggerDoesNotBlockIntegrationsAsync(services.GetRequiredService<TmsDbContext>(), shutdown.Token);

var exitCode = jobKind switch
{
    "tachomaster" => await runner.RunAsync("TachoMaster", "job:tachomaster", TimeSpan.FromMinutes(70),
        services.GetRequiredService<TachoMasterScheduledJob>().RunAsync, shutdown.Token),
    "fleetio" => await runner.RunAsync("Fleetio", "job:fleetio", TimeSpan.FromMinutes(55), async ct =>
    {
        var result = await services.GetRequiredService<IntegrationSyncCoordinator>().SyncFleetioAsync("system:aca-job:fleetio", ct);
        return new JobExecutionResult(result.Success, result.Message, result.Changed);
    }, shutdown.Token),
    "sagehr" => await runner.RunAsync("SageHR", "job:sagehr", TimeSpan.FromMinutes(45),
        services.GetRequiredService<SageHrScheduledJob>().RunAsync, shutdown.Token),
    "eta" => await runner.RunAsync("ETARecalculation", "job:eta-recalculation", TimeSpan.FromMinutes(10),
        services.GetRequiredService<EtaRecalculationJob>().RunAsync, shutdown.Token),
    _ => throw new InvalidOperationException($"Unsupported TMS_JOB_KIND '{jobKind}'. Expected tachomaster, fleetio, sagehr or eta.")
};

Environment.ExitCode = exitCode;

static async Task EnsureStagedImportTriggerDoesNotBlockIntegrationsAsync(TmsDbContext db, CancellationToken ct)
{
    if (!db.Database.IsRelational()) return;

    // Migration 073 added a rejected-order guard trigger to StagedImports. That table is also
    // used by TachoMaster, Sage HR, Fleetio and master-data evidence. SQL Server rejects EF's
    // generated OUTPUT DML against trigger-enabled tables, so scheduled integrations must remove
    // this runtime trigger before any provider sync starts. Rejected-order learning protection
    // remains in the API/service path rather than a shared table trigger.
    await db.Database.ExecuteSqlRawAsync("""
        IF OBJECT_ID(N'dbo.TR_StagedImports_Rejected_DoNotLearn', N'TR') IS NOT NULL
            DROP TRIGGER dbo.TR_StagedImports_Rejected_DoNotLearn;
        """, ct);
}

static string ReadSetting(IConfiguration configuration, string fallback, params string[] keys) =>
    keys.Select(key => configuration[key]).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? fallback;

static bool ReadBool(IConfiguration configuration, bool fallback, params string[] keys) =>
    bool.TryParse(ReadSetting(configuration, fallback.ToString(), keys), out var value) ? value : fallback;
