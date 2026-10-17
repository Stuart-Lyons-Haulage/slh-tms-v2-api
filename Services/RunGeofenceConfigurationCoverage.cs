using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Calculates configuration coverage independently from runtime geofence evidence.
/// A run is configured/linked when at least one of its planned stops resolves through
/// Site Master to an active linked geofence. Runtime hits are deliberately not used in
/// this calculation so a telemetry fault cannot make configured linkage appear to be 0.
/// </summary>
public static class RunGeofenceConfigurationCoverage
{
    public static async Task<RunGeofenceCoverage> CalculateAsync(
        TmsDbContext db,
        IReadOnlyCollection<Load> loads,
        CancellationToken ct)
    {
        var activeGeofenceCount = 0;
        try
        {
            activeGeofenceCount = await db.SiteGeofences.AsNoTracking()
                .CountAsync(fence => fence.Active, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        PlannerSourceMasterDataResolver? resolver = null;
        try
        {
            resolver = await PlannerSourceMasterDataResolver.CreateAsync(db, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        if (resolver is null)
            return new RunGeofenceCoverage(activeGeofenceCount, 0, 0, loads.SelectMany(load => load.Stops ?? []).Count());

        var linkedRuns = 0;
        var linkedStops = 0;
        var totalStops = 0;

        foreach (var load in loads.Where(load => load.Status != LoadStatus.Cancelled))
        {
            var runLinked = false;
            foreach (var stop in load.Stops ?? [])
            {
                totalStops++;
                var resolution = resolver.Resolve(stop.Name);
                if (!resolution.GeofenceLinked) continue;
                linkedStops++;
                runLinked = true;
            }
            if (runLinked) linkedRuns++;
        }

        return new RunGeofenceCoverage(activeGeofenceCount, linkedRuns, linkedStops, totalStops);
    }
}

public sealed record RunGeofenceCoverage(
    int ActiveGeofenceCount,
    int LinkedRuns,
    int LinkedStops,
    int TotalStops);
