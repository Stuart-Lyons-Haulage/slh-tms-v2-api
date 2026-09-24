using System.Security.Claims;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Slh.Tms.Api.Authorization;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Hubs;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Models.Assistant;
using Slh.Tms.Api.Services;

[assembly: InternalsVisibleTo("Slh.Tms.Api.Tests")]

var builder = WebApplication.CreateBuilder(args);
var authMode = (builder.Configuration["Auth:Mode"] ?? "Entra").Trim();
var localAuthMode = string.Equals(authMode, "Local", StringComparison.OrdinalIgnoreCase);
var tenantId = builder.Configuration["Entra:TenantId"];
var audience = localAuthMode
    ? (builder.Configuration["Auth:Local:Audience"] ?? "slh-tms-v2")
    : builder.Configuration["Entra:Audience"];
if (!localAuthMode && string.IsNullOrWhiteSpace(tenantId))
    throw new InvalidOperationException("Entra:TenantId is required when Auth:Mode is Entra.");
if (string.IsNullOrWhiteSpace(audience))
    throw new InvalidOperationException("Authentication audience is required.");
var allowedTmsDomains = builder.Configuration.GetSection("Entra:AllowedDomains").Get<string[]>() ?? ["lyonshaulage.com"];
var deploymentRevision = builder.Configuration["Deployment:Revision"] ?? "local";
var applicationInsightsConnectionString =
    builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] ??
    builder.Configuration["ApplicationInsights:ConnectionString"];

var openTelemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "slh-tms-api",
        serviceVersion: deploymentRevision));

openTelemetry.WithTracing(tracing =>
{
    tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSqlClientInstrumentation();
    if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
        tracing.AddAzureMonitorTraceExporter(options => options.ConnectionString = applicationInsightsConnectionString);
});

openTelemetry.WithMetrics(metrics =>
{
    metrics
        .AddMeter(TmsMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation();
    if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
        metrics.AddAzureMonitorMetricExporter(options => options.ConnectionString = applicationInsightsConnectionString);
});

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
        logging.AddAzureMonitorLogExporter(options => options.ConnectionString = applicationInsightsConnectionString);
});

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddSingleton(_ =>
{
    var telemetryConfiguration = TelemetryConfiguration.CreateDefault();
    telemetryConfiguration.ConnectionString = applicationInsightsConnectionString;
    telemetryConfiguration.DisableTelemetry = string.IsNullOrWhiteSpace(applicationInsightsConnectionString);
    return new TelemetryClient(telemetryConfiguration);
});
builder.Services.Configure<HgvVehicleProfile>(builder.Configuration.GetSection("Routing:HgvVehicleProfile"));
builder.Services.Configure<AzureMapsMatrixOptions>(builder.Configuration.GetSection("Routing:AzureMapsMatrix"));
builder.Services.Configure<BackloadMatchingOptions>(builder.Configuration.GetSection("Optimisation:Backload"));
builder.Services.Configure<LiveEtaOptions>(builder.Configuration.GetSection("Eta:Live"));
builder.Services.Configure<FuelCostOptions>(builder.Configuration.GetSection("Fuel:Costing"));

var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()?
    .Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Select(origin => origin.Trim())
    .ToArray();
var allowedOrigins = configuredOrigins is { Length: > 0 } ? configuredOrigins : [
    "https://slh-tms-portal-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io"
];
builder.Services.AddCors(options => options.AddPolicy("Portal", policy => policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton(TmsMetrics.Shared);
builder.Services.AddSingleton<SqlLatencyInterceptor>();
builder.Services.AddSingleton<PlanningChangeNotifier>();
builder.Services.AddSingleton<OutboundHttpPolicyRegistry>();
builder.Services.AddScoped<DependencyHealthService>();
builder.Services.AddHostedService<DependencyTelemetrySampler>();
builder.Services.AddDbContext<TmsDbContext>((services, options) =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("TmsDb"))
        .AddInterceptors(services.GetRequiredService<SqlLatencyInterceptor>()));
builder.Services.AddScoped<LocalAuthService>();
builder.Services.AddScoped<StagingService>();
builder.Services.AddScoped<MasterDataService>();
builder.Services.AddScoped<MasterAssignmentComplianceService>();
builder.Services.AddSingleton<CustomerCommunicationExtractionService>();
builder.Services.AddScoped<OrderIntakeLedgerService>();
builder.Services.AddScoped<IntakeMappingService>();
builder.Services.AddScoped<OrderCompletenessService>();
builder.Services.AddScoped<WarehouseMovementService>();
builder.Services.AddScoped<PlanningOptimiserService>();
var dispatchOptions = new DispatchOptions();
builder.Configuration.GetSection("Dispatch").Bind(dispatchOptions);
builder.Services.AddSingleton(dispatchOptions);
builder.Services.AddScoped<DispatchService>();
builder.Services.AddScoped<IBetaHgvRouteProvider, AzureMapsHgvRouteProvider>();
builder.Services.AddScoped<BetaRouteOptimisationEngine>();
builder.Services.AddScoped<BetaOptimiserService>();
builder.Services.AddScoped<SiteTimingRuleStore>();
builder.Services.AddScoped<DotTrackingTelemetryStore>();
builder.Services.AddScoped<IAzureMapsMatrixService, AzureMapsMatrixService>();
builder.Services.AddScoped<IBackloadMatchingService, BackloadMatchingService>();
builder.Services.AddScoped<BackloadOperationsService>();
builder.Services.AddScoped<LiveEtaCalculator>();
builder.Services.AddScoped<EtaAccuracyProcessor>();
builder.Services.AddScoped<CustomerNotificationService>();
builder.Services.AddScoped<FuelOptimisationService>();
builder.Services.AddSingleton<RoadTechLiveSnapshot>();
var assistantOptions = new AssistantOptions();
builder.Configuration.GetSection("Integrations:OpenAI").Bind(assistantOptions);
assistantOptions.Enabled = ReadBool(builder.Configuration, assistantOptions.Enabled,
    "Integrations:OpenAI:Enabled", "Integrations__OpenAI__Enabled", "openai-enabled", "OpenAI--Enabled");
assistantOptions.ApiKey = ReadSetting(builder.Configuration, assistantOptions.ApiKey,
    "Integrations:OpenAI:ApiKey", "Integrations__OpenAI__ApiKey", "openai-api-key", "OpenAI--ApiKey");
assistantOptions.Model = ReadSetting(builder.Configuration, assistantOptions.Model,
    "Integrations:OpenAI:Model", "Integrations__OpenAI__Model", "openai-model", "OpenAI--Model");
builder.Services.AddSingleton(assistantOptions);
builder.Services.AddHttpClient<TmsAssistantService>();

var dotTrackingOptions = new DotTrackingOptions();
builder.Configuration.GetSection("Tracking:Dot").Bind(dotTrackingOptions);
builder.Services.AddSingleton(dotTrackingOptions);
var tachoMasterOptions = new TachoMasterOptions();
builder.Configuration.GetSection("Integrations:TachoMaster").Bind(tachoMasterOptions);
tachoMasterOptions.Enabled = ReadBool(builder.Configuration, tachoMasterOptions.Enabled,
    "Integrations:TachoMaster:Enabled", "Integrations__TachoMaster__Enabled", "tachomaster-enabled", "tacho-enabled", "TachoMaster--Enabled");
tachoMasterOptions.BaseUrl = ReadSetting(builder.Configuration, tachoMasterOptions.BaseUrl,
    "Integrations:TachoMaster:BaseUrl", "Integrations__TachoMaster__BaseUrl", "tachomaster-base-url", "tacho-base-url", "TachoMaster--BaseUrl");
tachoMasterOptions.ApiKey = ReadSetting(builder.Configuration, tachoMasterOptions.ApiKey,
    "Integrations:TachoMaster:ApiKey", "Integrations__TachoMaster__ApiKey", "tachomaster-api-key", "tacho-api-key", "TachoMaster--ApiKey");
tachoMasterOptions.Username = ReadSetting(builder.Configuration, tachoMasterOptions.Username,
    "Integrations:TachoMaster:Username", "Integrations__TachoMaster__Username", "tachomaster-username", "tacho-username", "TachoMaster--Username");
tachoMasterOptions.Password = ReadSetting(builder.Configuration, tachoMasterOptions.Password,
    "Integrations:TachoMaster:Password", "Integrations__TachoMaster__Password", "tachomaster-password", "tacho-password", "TachoMaster--Password");
var hasDedicatedTachoCredentials = !string.IsNullOrWhiteSpace(tachoMasterOptions.ApiKey) ||
    !string.IsNullOrWhiteSpace(tachoMasterOptions.Username) ||
    !string.IsNullOrWhiteSpace(tachoMasterOptions.Password);
if (!hasDedicatedTachoCredentials && dotTrackingOptions.IsConfigured)
{
    tachoMasterOptions.Enabled = true;
    tachoMasterOptions.BaseUrl = dotTrackingOptions.BaseUrl;
    tachoMasterOptions.ApiKey = dotTrackingOptions.ApiKey;
    tachoMasterOptions.Username = dotTrackingOptions.Username;
    tachoMasterOptions.Password = dotTrackingOptions.Password;
    tachoMasterOptions.UsesSharedRoadTechCredentials = true;
}
builder.Services.AddSingleton(tachoMasterOptions);
var sageHrOptions = new SageHrOptions();
builder.Configuration.GetSection("Integrations:SageHr").Bind(sageHrOptions);
builder.Services.AddSingleton(sageHrOptions);
var azureSmsOptions = new AzureSmsOptions();
builder.Configuration.GetSection("Integrations:AzureSms").Bind(azureSmsOptions);
builder.Services.AddSingleton(azureSmsOptions);
var textBeeOptions = new TextBeeOptions();
builder.Configuration.GetSection("Integrations:TextBee").Bind(textBeeOptions);
builder.Services.AddSingleton(textBeeOptions);
var fleetioOptions = new FleetioOptions();
builder.Configuration.GetSection("Integrations:Fleetio").Bind(fleetioOptions);
fleetioOptions.Enabled = ReadBool(builder.Configuration, fleetioOptions.Enabled,
    "Integrations:Fleetio:Enabled", "Integrations__Fleetio__Enabled", "fleetio-enabled", "Fleetio--Enabled");
fleetioOptions.BaseUrl = ReadSetting(builder.Configuration, fleetioOptions.BaseUrl,
    "Integrations:Fleetio:BaseUrl", "Integrations__Fleetio__BaseUrl", "fleetio-base-url", "Fleetio--BaseUrl");
fleetioOptions.ApiKey = ReadSetting(builder.Configuration, fleetioOptions.ApiKey,
    "Integrations:Fleetio:ApiKey", "Integrations__Fleetio__ApiKey", "fleetio-api-key", "Fleetio--ApiKey");
fleetioOptions.AccountToken = ReadSetting(builder.Configuration, fleetioOptions.AccountToken,
    "Integrations:Fleetio:AccountToken", "Integrations__Fleetio__AccountToken", "fleetio-account-token", "Fleetio--AccountToken");
fleetioOptions.ApiVersion = ReadSetting(builder.Configuration, fleetioOptions.ApiVersion,
    "Integrations:Fleetio:ApiVersion", "Integrations__Fleetio__ApiVersion", "fleetio-api-version", "Fleetio--ApiVersion");
if (fleetioOptions.BaseUrl.EndsWith("/api/v2", StringComparison.OrdinalIgnoreCase)) fleetioOptions.BaseUrl = fleetioOptions.BaseUrl[..^1] + "1";
builder.Services.AddSingleton(fleetioOptions);
builder.Services.AddScoped<AzureSmsDispatchService>();
builder.Services.AddScoped<DistributedLeaseManager>();
builder.Services.AddScoped<IntegrationSyncCoordinator>();
builder.Services.AddScoped<TachoDriverMasterSyncService>();
builder.Services.AddScoped<TachoDriverHoursRefreshService>();
builder.Services.AddScoped<DriverMasterClassificationService>();
builder.Services.AddScoped<TachoCanonicalDriverMasterOrchestrator>();
builder.Services.AddScoped<TachoDriverMasterSyncJobService>();
builder.Services.AddTransient<TachoMasterRetryHandler>();
builder.Services.AddTransient<TachoMasterResponseCacheHandler>();
builder.Services.AddTransient<DependencyTelemetryHandler>();
builder.Services.AddTransient<ProviderResilienceHandler>();
builder.Services.AddScoped<DriverWeeklyRestComplianceService>();
builder.Services.AddHttpClient<DriverSmsDispatchService>().AddHttpMessageHandler<DependencyTelemetryHandler>().AddHttpMessageHandler<ProviderResilienceHandler>();
builder.Services.AddHttpClient<SageHrClient>().AddHttpMessageHandler<DependencyTelemetryHandler>().AddHttpMessageHandler<ProviderResilienceHandler>();
builder.Services.AddHttpClient<DotTrackingClient>()
    .AddHttpMessageHandler<DependencyTelemetryHandler>()
    .AddPolicyHandler((services, _) => services.GetRequiredService<OutboundHttpPolicyRegistry>().Get("DOT/RoadTech"));
builder.Services.AddHttpClient<TachoMasterClient>()
    .AddHttpMessageHandler<DependencyTelemetryHandler>()
    .AddHttpMessageHandler<TachoMasterResponseCacheHandler>()
    .AddHttpMessageHandler<TachoMasterRetryHandler>()
    .ConfigureHttpClient((sp, _) =>
    {
        sp.GetService<DotTrackingClient>();
    });
builder.Services.AddHttpClient<AzureMapsRouteClient>()
    .AddHttpMessageHandler<DependencyTelemetryHandler>()
    .AddPolicyHandler((services, _) => services.GetRequiredService<OutboundHttpPolicyRegistry>().Get("Azure Maps"));
builder.Services.AddHttpClient("AzureMapsMatrix", client =>
    client.BaseAddress = new Uri((builder.Configuration["Maps:Endpoint"] ?? "https://atlas.microsoft.com").TrimEnd('/')))
    .AddHttpMessageHandler<DependencyTelemetryHandler>()
    .AddPolicyHandler((services, _) => services.GetRequiredService<OutboundHttpPolicyRegistry>().Get("Azure Maps"));
builder.Services.AddHttpClient<FleetioClient>()
    .AddHttpMessageHandler<DependencyTelemetryHandler>()
    .AddPolicyHandler((services, _) => services.GetRequiredService<OutboundHttpPolicyRegistry>().Get("Fleetio"));

// Provider polling and derived-data maintenance must never take the operational
// API down. Each worker already records and retries its own failures; this is a
// final host-level guard for an unexpected worker termination.
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

builder.Services.AddHostedService<DotTrackingIngestionService>();
builder.Services.AddHostedService<TachoDriverMasterSyncJobWorker>();
builder.Services.AddHostedService<TachoDriverHoursRefreshWorker>();
builder.Services.AddHostedService<DriverMasterClassificationBackgroundService>();
builder.Services.AddHostedService<AuditOutboxBackgroundService>();
builder.Services.AddHostedService<BackloadTriggerHostedService>();
builder.Services.AddHostedService<LiveEtaService>();
builder.Services.AddHostedService<EtaAccuracyService>();

builder.Services.AddHealthChecks().AddDbContextCheck<TmsDbContext>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    if (localAuthMode)
    {
        var localSigningKey = builder.Configuration["Auth:Local:SigningKey"];
        if (string.IsNullOrWhiteSpace(localSigningKey) || localSigningKey.Length < 32)
            throw new InvalidOperationException("Auth:Local:SigningKey must be at least 32 characters when Auth:Mode is Local.");

        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Auth:Local:Issuer"] ?? "slh-tms-v2",
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(localSigningKey)),
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role
        };
    }
    else
    {
        o.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = new[]
            {
                $"https://login.microsoftonline.com/{tenantId}/v2.0",
                $"https://sts.windows.net/{tenantId}/"
            },
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true
        };
    }

    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            var accessToken = ctx.Request.Query["access_token"];
            var path = ctx.HttpContext.Request.Path;
            if (!string.IsNullOrWhiteSpace(accessToken) &&
                (path.StartsWithSegments("/dispatch-hub") || path.StartsWithSegments("/eta-hub")))
                ctx.Token = accessToken;
            return Task.CompletedTask;
        },
        OnAuthenticationFailed = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        },
        OnChallenge = ctx =>
        {
            if (!ctx.Handled) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization(options =>
{
    var tmsAccessPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context => TmsAccessPolicy.IsCompanyUser(context.User, allowedTmsDomains))
        .Build();
    options.DefaultPolicy = tmsAccessPolicy;
    options.FallbackPolicy = tmsAccessPolicy;
    options.AddPolicy("TmsAccess", tmsAccessPolicy);
    options.AddPolicy("TmsRead", tmsAccessPolicy);
    options.AddPolicy("TmsWrite", tmsAccessPolicy);
    options.AddPolicy("TmsApprove", tmsAccessPolicy);
    options.AddPolicy("TmsReadMaster", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => TmsMasterRolePolicy.CanReadMaster(context.User, allowedTmsDomains)));
    options.AddPolicy("TmsAdmin", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => TmsMasterRolePolicy.IsAdmin(context.User, allowedTmsDomains)));
});

static string ReadSetting(IConfiguration configuration, string fallback, params string[] keys) =>
    keys.Select(key => configuration[key]).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? fallback;

static bool ReadBool(IConfiguration configuration, bool fallback, params string[] keys) =>
    bool.TryParse(ReadSetting(configuration, fallback.ToString(), keys), out var value) ? value : fallback;

var app = builder.Build();

// Production must never modify the operational schema or master data merely because
// a replica starts.  A controlled release may opt in only after a verified restore
// point, schema review and record-count check.
var applySchemaChangesOnStartup = builder.Configuration.GetValue<bool>("Database:ApplySchemaChangesOnStartup");
if (!app.Environment.IsEnvironment("Testing") && applySchemaChangesOnStartup)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Tms.SchemaMigration");
    await SchemaMigrationRunner.ApplyAsync(db, logger, CancellationToken.None);
    try
    {
        await ManagementReportingStore.EnsureSchemaAsync(db, CancellationToken.None);
        await CustomerNotificationStore.EnsureSchemaAsync(db, CancellationToken.None);
        var quarantinedFleetioPlaceholders = await MasterDetailStore.QuarantineFleetioPlaceholdersAsync(db, CancellationToken.None);
        if (quarantinedFleetioPlaceholders > 0)
            logger.LogWarning("Quarantined {PlaceholderCount} Fleetio placeholder vehicle records from operational master data.", quarantinedFleetioPlaceholders);
        var trailerMerge = await MasterDetailStore.MergeSlhTrailerAliasesAsync(db, CancellationToken.None);
        if (trailerMerge.Renamed > 0 || trailerMerge.Merged > 0)
            logger.LogWarning(
                "Canonicalised trailer register: {Renamed} numeric trailers renamed, {Merged} duplicate aliases merged, {LoadsReassigned} loads, {MappingsReassigned} mappings and {AuditEntriesReassigned} audit entries reassigned.",
                trailerMerge.Renamed, trailerMerge.Merged, trailerMerge.LoadsReassigned, trailerMerge.MappingsReassigned, trailerMerge.AuditEntriesReassigned);
        var register = scope.ServiceProvider.GetRequiredService<StagingService>();
        await register.LinkRegistered(25, CancellationToken.None);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Post-migration startup maintenance failed; required schema migrations completed successfully, so application startup will continue.");
    }
}

if (localAuthMode)
{
    var bootstrapUsername = builder.Configuration["Auth:Local:BootstrapUsername"];
    var bootstrapPassword = builder.Configuration["Auth:Local:BootstrapPassword"];
    var bootstrapDisplayName = builder.Configuration["Auth:Local:BootstrapDisplayName"] ?? "SLH Administrator";
    if (!string.IsNullOrWhiteSpace(bootstrapUsername) && !string.IsNullOrWhiteSpace(bootstrapPassword))
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        if (!await db.TmsUsers.AnyAsync())
        {
            var auth = scope.ServiceProvider.GetRequiredService<LocalAuthService>();
            await auth.CreateAsync(bootstrapUsername, bootstrapDisplayName, bootstrapPassword, "TMS.Admin", CancellationToken.None);
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Tms.LocalAuth");
            logger.LogInformation("Created initial local TMS administrator account {Username}.", LocalAuthService.NormaliseUsername(bootstrapUsername));
        }
    }
}

app.UseHttpsRedirection();
app.UseCors("Portal");
app.UseMiddleware<Slh.Tms.Api.Middleware.ApiLatencyMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<Slh.Tms.Api.Middleware.PlanningChangeNotificationMiddleware>();
app.UseMiddleware<Slh.Tms.Api.Middleware.PlanningControlResilienceMiddleware>();
app.UseMiddleware<Slh.Tms.Api.Middleware.SiteLookupResilienceMiddleware>();
app.UseMiddleware<Slh.Tms.Api.Middleware.PlanLockMiddleware>();

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "healthy", revision = deploymentRevision })).AllowAnonymous();
app.MapHealthChecks("/api/v1/health/ready", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        if (report.Status != Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Tms.SqlReadiness");
            foreach (var entry in report.Entries)
                logger.LogError(entry.Value.Exception, "Health check {HealthCheckName} returned {HealthStatus}.", entry.Key, entry.Value.Status);
        }
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() });
    }
}).AllowAnonymous();

app.MapControllers();
app.MapHub<DispatchHub>("/dispatch-hub");
app.MapHub<EtaHub>("/eta-hub");

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.Run();

public partial class Program { }