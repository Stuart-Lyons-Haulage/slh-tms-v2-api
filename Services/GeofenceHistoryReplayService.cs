using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class GeofenceHistoryReplayService(
    DotTrackingClient client,
    DotTrackingTelemetryStore store,
    TmsDbContext db,
    ILogger<GeofenceHistoryReplayService> logger)
{
    private static readonly string[] PrioritySites = ["Lake Lane", "NWF Selsey", "NWF Runcton", "NWF Merston", "NWF Drayton"];

    public async Task<GeofenceHistoryReplayResult> ReplayAsync(DateOnly planningDate, CancellationToken ct)
    {
        var providerHistory = new List<DotTelemetryRecord>();
        try
        {
            providerHistory = (await client.GetHistoricalVehicleEventsAsync(planningDate, ct))
                .Select(DotTelemetryRecord.FromProvider)
                .ToList();
            await store.PersistAsync(providerHistory, ct, markAsLiveReceipt: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "RoadTech provider history could not be refreshed for {PlanningDate}; stored SQL telemetry will still be replayed.", planningDate);
        }

        var storedHistoryCount = await CountStoredHistoryAsync(planningDate, ct);
        var loads = await ReadLoadsAsync(planningDate, ct);
        if (loads.Count == 0)
            return new GeofenceHistoryReplayResult(planningDate, storedHistoryCount, 0, 0, 0, EmptySiteDiagnostics(), DateTimeOffset.UtcNow, storedHistoryCount, providerHistory.Count);

        // BuildAsync reads the persisted VehicleTrackingEvents for only the vehicles on the
        // operating day's runs, in chronological order, and uses the current active SQL
        // SiteGeofences. Persisting that derived snapshot is safer than feeding old events
        // into the live state machine, where an open current visit could be mixed with an
        // earlier historical point.
        var geofenceLoads = GeofencePlanningMatch.PrepareLoads(loads);
        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, geofenceLoads, ct);
        await EmbeddedGeofenceSqlProjection.PersistAsync(db, snapshot, ct);
        snapshot = await EmbeddedGeofenceEvidenceMerge.MergeDurableProjectionAsync(db, snapshot, loads, ct);

        var siteDiagnostics = PrioritySites
            .Select(site =>
            {
                var visits = snapshot.Visits.Where(x => CanonicalFenceName(x.Fence.Name) == site).ToList();
                return new GeofenceHistoryReplaySiteDiagnostic(
                    site,
                    PlannedStops(loads, site),
                    visits.Count,
                    visits.Count(x => x.LoadStopId is not null),
                    visits.Count(x => x.ExitedAtUtc is not null),
                    visits.Select(x => (DateTimeOffset?)(x.ExitedAtUtc ?? x.LastInsideAtUtc)).OrderByDescending(x => x).FirstOrDefault());
            })
            .ToList();

        logger.LogInformation(
            "Replayed stored RoadTech history ({StoredCount} records in operating window; {ProviderCount} provider records refreshed first) for {PlanningDate} through the current SQL geofence projection; reconstructed {VisitCount} durable visits, {LinkedCount} linked visits and {DepartureCount} departures. Priority sites: {PrioritySites}.",
            storedHistoryCount,
            providerHistory.Count,
            planningDate,
            snapshot.Visits.Count,
            snapshot.Visits.Count(x => x.LoadStopId is not null),
            snapshot.Visits.Count(x => x.ExitedAtUtc is not null),
            string.Join(", ", siteDiagnostics.Select(x => $"{x.Site}={x.Visits}/{x.LinkedVisits}/{x.Departures}")));

        return new GeofenceHistoryReplayResult(
            planningDate,
            storedHistoryCount,
            snapshot.Visits.Count,
            snapshot.Visits.Count(x => x.LoadStopId is not null),
            snapshot.Visits.Count(x => x.ExitedAtUtc is not null),
            siteDiagnostics,
            DateTimeOffset.UtcNow,
            storedHistoryCount,
            providerHistory.Count);
    }

    private async Task<int> CountStoredHistoryAsync(DateOnly planningDate, CancellationToken ct)
    {
        var (fromUtc, toUtc) = UtcOperatingWindow(planningDate);
        return await db.VehicleTrackingEvents.AsNoTracking()
            .CountAsync(row => row.EventTimeUtc >= fromUtc.AddHours(-2) && row.EventTimeUtc < toUtc.AddHours(12), ct);
    }

    private async Task<List<Load>> ReadLoadsAsync(DateOnly planningDate, CancellationToken ct)
    {
        var merged = new Dictionary<Guid, Load>();
        try
        {
            foreach (var load in await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct))
                merged[load.Id] = load;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "Planning register could not be read during geofence history replay for {PlanningDate}; live Loads will still be tried.", planningDate);
        }

        try
        {
            var live = await db.Loads.AsNoTracking()
                .Include(load => load.Stops)
                .Where(load => load.PlanningDate == planningDate)
                .ToListAsync(ct);
            foreach (var load in live) merged[load.Id] = load;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "Live Loads could not be read during geofence history replay for {PlanningDate}; planning-register loads will still be used.", planningDate);
        }

        return merged.Values.Where(load => load.Status != LoadStatus.Cancelled).ToList();
    }

    private static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) UtcOperatingWindow(DateOnly planningDate)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            var fromLocal = planningDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            var toLocal = planningDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return (new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(fromLocal, zone)), new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(toLocal, zone)));
        }
        catch (TimeZoneNotFoundException)
        {
            var from = new DateTimeOffset(planningDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            return (from, from.AddDays(1));
        }
    }

    private static IReadOnlyList<GeofenceHistoryReplaySiteDiagnostic> EmptySiteDiagnostics() =>
        PrioritySites.Select(site => new GeofenceHistoryReplaySiteDiagnostic(site, 0, 0, 0, 0, null)).ToList();

    private static int PlannedStops(IEnumerable<Load> loads, string site)
    {
        if (site == "Lake Lane") return 0;
        var locality = site.Replace("NWF ", string.Empty, StringComparison.OrdinalIgnoreCase);
        return loads.SelectMany(load => load.Stops ?? [])
            .Count(stop => stop.Name.Contains(locality, StringComparison.OrdinalIgnoreCase));
    }

    private static string? CanonicalFenceName(string value)
    {
        var normalized = value.ToUpperInvariant();
        if (normalized.Contains("LAKE LANE")) return "Lake Lane";
        if (normalized.Contains("SELSEY")) return "NWF Selsey";
        if (normalized.Contains("RUNCTON")) return "NWF Runcton";
        if (normalized.Contains("MERSTON")) return "NWF Merston";
        if (normalized.Contains("DRAYTON")) return "NWF Drayton";
        return null;
    }
}

public sealed record GeofenceHistoryReplayResult(
    DateOnly PlanningDate,
    int HistoricalTrackingRecords,
    int ReconstructedVisits,
    int LinkedVisits,
    int Departures,
    IReadOnlyList<GeofenceHistoryReplaySiteDiagnostic> PrioritySites,
    DateTimeOffset CompletedAtUtc,
    int StoredTrackingRecords = 0,
    int ProviderTrackingRecords = 0);

public sealed record GeofenceHistoryReplaySiteDiagnostic(
    string Site,
    int PlannedStops,
    int Visits,
    int LinkedVisits,
    int Departures,
    DateTimeOffset? LatestEvidenceUtc);
