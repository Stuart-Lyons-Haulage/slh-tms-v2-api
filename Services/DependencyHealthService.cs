using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record DependencyState(string Status, DateTimeOffset? LastSuccessfulContactUtc, double? AgeSeconds, string? Detail = null);
public sealed record DependencyHealthSnapshot(DateTimeOffset CheckedAtUtc, string Status, IReadOnlyDictionary<string, DependencyState> Dependencies);

public sealed class DependencyHealthService(
    TmsDbContext db,
    DotTrackingOptions dot,
    FleetioOptions fleetio,
    TachoMasterOptions tacho,
    SageHrClient sage,
    ILogger<DependencyHealthService> logger)
{
    public static readonly IReadOnlyDictionary<string, string> CanonicalCadences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["RoadTech"] = "live every minute · history every 5 minutes",
        ["TachoMaster"] = "every 5 minutes",
        ["Fleetio"] = "every hour",
        ["Sage HR"] = "05:30 Europe/London daily"
    };

    public async Task<DependencyHealthSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        bool sqlAvailable;
        try { sqlAvailable = await db.Database.CanConnectAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dependency health SQL connectivity check failed.");
            sqlAvailable = false;
        }

        if (!sqlAvailable)
        {
            var unavailable = new Dictionary<string, DependencyState>(StringComparer.OrdinalIgnoreCase)
            {
                ["SQL"] = new("Unavailable", null, null, "Azure SQL could not be reached."),
                ["RoadTech"] = new("Unavailable", null, null, "Freshness could not be read because SQL is unavailable."),
                ["Fleetio"] = new("Unavailable", null, null, "Freshness could not be read because SQL is unavailable."),
                ["TachoMaster"] = new("Unavailable", null, null, "Freshness could not be read because SQL is unavailable."),
                ["Sage HR"] = new("Unavailable", null, null, "Freshness could not be read because SQL is unavailable.")
            };
            return new(now, "Unavailable", unavailable);
        }

        // These are the canonical persisted receipts used everywhere in the TMS. Reading health
        // must never call RoadTech, TachoMaster, Fleetio or Sage directly and therefore cannot
        // make an old provider snapshot appear newer just because a page was refreshed.
        var roadTechUtc = await SafeTimestamp(async () => await db.VehicleLiveStatuses.AsNoTracking()
            .MaxAsync(item => (DateTimeOffset?)item.LastEventTimeUtc, ct), "RoadTech", ct);
        var fleetioMappingUtc = await SafeTimestamp(async () => await db.IntegrationMappings.AsNoTracking()
            .Where(item => item.Provider == "Fleetio" && item.Active)
            .MaxAsync(item => (DateTimeOffset?)item.UpdatedAtUtc, ct), "Fleetio mappings", ct);
        var fleetioVehicleUtc = await SafeTimestamp(async () => await db.Vehicles.AsNoTracking()
            .Where(item => item.Active && item.FleetioLastSyncedUtc != null)
            .MaxAsync(item => item.FleetioLastSyncedUtc, ct), "Fleetio vehicles", ct);
        var fleetioUtc = Latest(fleetioMappingUtc, fleetioVehicleUtc);
        var tachoAuditUtc = await SafeTimestamp(async () => await db.StagedImports.AsNoTracking()
            .Where(item => item.Status == StagingStatus.Promoted &&
                (item.EntityType == "tachodrivermastersync" || item.EntityType == "tachomastersync" || item.EntityType == "tachodriverprofile"))
            .MaxAsync(item => (DateTimeOffset?)(item.ReviewedAtUtc ?? item.ReceivedAtUtc), ct), "TachoMaster audit", ct);
        var tachoDriverUtc = await SafeTimestamp(async () => await db.Drivers.AsNoTracking()
            .Where(item => item.Active && item.LastTachoSyncUtc != null)
            .MaxAsync(item => item.LastTachoSyncUtc, ct), "TachoMaster driver state", ct);
        var tachoUtc = Latest(tachoAuditUtc, tachoDriverUtc);
        var sageUtc = await SafeTimestamp(async () => await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "sagehrsync" && item.Status == StagingStatus.Promoted)
            .MaxAsync(item => (DateTimeOffset?)(item.ReviewedAtUtc ?? item.ReceivedAtUtc), ct), "Sage HR", ct);

        var dependencies = new Dictionary<string, DependencyState>(StringComparer.OrdinalIgnoreCase)
        {
            ["SQL"] = new("Healthy", now, 0),
            ["RoadTech"] = Evaluate(dot.IsConfigured, roadTechUtc, now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
            ["TachoMaster"] = Evaluate(tacho.IsConfigured, tachoUtc, now, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)),
            ["Fleetio"] = Evaluate(fleetio.IsConfigured, fleetioUtc, now, TimeSpan.FromMinutes(75), TimeSpan.FromHours(3)),
            ["Sage HR"] = Evaluate(sage.IsConfigured, sageUtc, now, TimeSpan.FromHours(30), TimeSpan.FromHours(48))
        };

        var overall = dependencies.Values.Any(item => item.Status == "Unavailable")
            ? "Unavailable"
            : dependencies.Values.Any(item => item.Status == "Degraded") ? "Degraded" : "Healthy";
        return new(now, overall, dependencies);
    }

    internal static DependencyState Evaluate(bool configured, DateTimeOffset? lastSuccessfulContactUtc, DateTimeOffset now, TimeSpan healthyAge, TimeSpan unavailableAge)
    {
        if (!configured) return new("Unavailable", lastSuccessfulContactUtc, Age(lastSuccessfulContactUtc, now), "Dependency is not configured.");
        if (lastSuccessfulContactUtc is null) return new("Unavailable", null, null, "No successful contact has been recorded.");
        var age = now - lastSuccessfulContactUtc.Value;
        if (age <= healthyAge) return new("Healthy", lastSuccessfulContactUtc, Math.Max(0, age.TotalSeconds));
        if (age <= unavailableAge) return new("Degraded", lastSuccessfulContactUtc, Math.Max(0, age.TotalSeconds), "Last successful contact is older than the normal schedule threshold.");
        return new("Unavailable", lastSuccessfulContactUtc, Math.Max(0, age.TotalSeconds), "Last successful contact is outside the dependency availability window.");
    }

    private async Task<DateTimeOffset?> SafeTimestamp(Func<Task<DateTimeOffset?>> query, string dependency, CancellationToken ct)
    {
        try { return await query(); }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read durable {Dependency} freshness timestamp.", dependency);
            db.ChangeTracker.Clear();
            return null;
        }
    }

    private static DateTimeOffset? Latest(params DateTimeOffset?[] values) =>
        values.Where(value => value is not null).Max();

    private static double? Age(DateTimeOffset? value, DateTimeOffset now) =>
        value is null ? null : Math.Max(0, (now - value.Value).TotalSeconds);
}
