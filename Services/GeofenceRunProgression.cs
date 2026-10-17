using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Planning;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public static class GeofenceRunProgression
{
    private const int DefaultConfirmDwellMinutes = 10;

    public static async Task EnsureSchemaAsync(TmsDbContext db, CancellationToken ct)
    {
        const string sql = """
IF OBJECT_ID(N'[dbo].[SiteGeofences]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[SiteGeofences](
        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
        [Name] nvarchar(200) NOT NULL,
        [NormalizedName] nvarchar(200) NOT NULL,
        [Category] nvarchar(80) NULL,
        [CategoryMaxWaitMinutes] int NULL,
        [MaxWaitMinutes] int NULL,
        [PendingEntryMinutes] int NOT NULL CONSTRAINT [DF_SiteGeofences_PendingEntryMinutes] DEFAULT 0,
        [PendingExitMinutes] int NOT NULL CONSTRAINT [DF_SiteGeofences_PendingExitMinutes] DEFAULT 0,
        [SiteNumber] nvarchar(40) NULL,
        [SiteId] uniqueidentifier NULL,
        [PolygonJson] nvarchar(max) NOT NULL,
        [Active] bit NOT NULL CONSTRAINT [DF_SiteGeofences_Active] DEFAULT 1,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        [UpdatedAtUtc] datetimeoffset NOT NULL
    );
    CREATE UNIQUE INDEX [IX_SiteGeofences_NormalizedName] ON [dbo].[SiteGeofences]([NormalizedName]);
    CREATE INDEX [IX_SiteGeofences_SiteId] ON [dbo].[SiteGeofences]([SiteId]);
END;
IF OBJECT_ID(N'[dbo].[GeofenceVisits]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[GeofenceVisits](
        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
        [GeofenceId] uniqueidentifier NOT NULL,
        [LoadId] uniqueidentifier NULL,
        [LoadStopId] uniqueidentifier NULL,
        [VehicleId] uniqueidentifier NULL,
        [VehicleIdentifier] nvarchar(80) NOT NULL,
        [EnteredAtUtc] datetimeoffset NOT NULL,
        [ConfirmedAtUtc] datetimeoffset NULL,
        [ExitedAtUtc] datetimeoffset NULL,
        [LastInsideAtUtc] datetimeoffset NOT NULL,
        [DwellMinutes] int NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [StatusReason] nvarchar(500) NULL,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        [UpdatedAtUtc] datetimeoffset NOT NULL
    );
    CREATE INDEX [IX_GeofenceVisits_Vehicle_Open] ON [dbo].[GeofenceVisits]([VehicleIdentifier],[ExitedAtUtc]);
    CREATE INDEX [IX_GeofenceVisits_Load_Stop] ON [dbo].[GeofenceVisits]([LoadId],[LoadStopId]);
    CREATE INDEX [IX_GeofenceVisits_Entered] ON [dbo].[GeofenceVisits]([EnteredAtUtc]);
END;
""";
        if (db.Database.IsRelational())
            await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    public static async Task<GeofenceImportResult> ImportFalconAsync(TmsDbContext db, JsonElement root, CancellationToken ct)
    {
        await EnsureSchemaAsync(db, ct);
        var category = Text(root, "category");
        var categoryMaxWait = Int(root, "category_max_wait_time");
        if (!root.TryGetProperty("geofences", out var geofences) || geofences.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Expected a Falcon geofence payload containing a geofences array.");

        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var inserted = 0; var updated = 0; var matched = 0;
        foreach (var item in geofences.EnumerateArray())
        {
            var name = Text(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!item.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array || points.GetArrayLength() < 3) continue;
            var normalized = Normalize(name);
            var existing = await db.SiteGeofences.SingleOrDefaultAsync(x => x.NormalizedName == normalized, ct);
            var site = MatchSite(name, sites);
            if (existing is null)
            {
                existing = new SiteGeofence { Name = name, NormalizedName = normalized, PolygonJson = points.GetRawText() };
                db.SiteGeofences.Add(existing); inserted++;
            }
            else updated++;
            existing.Name = name;
            existing.Category = category;
            existing.CategoryMaxWaitMinutes = categoryMaxWait;
            existing.MaxWaitMinutes = Int(item, "max_wait_time");
            existing.PendingEntryMinutes = Int(item, "pending_entry_minutes") ?? 0;
            existing.PendingExitMinutes = Int(item, "pending_exit_minutes") ?? 0;
            existing.SiteNumber = Text(item, "site_no");
            existing.PolygonJson = points.GetRawText();
            existing.SiteId = site?.Id;
            existing.Active = true;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (site is not null) matched++;
        }
        await db.SaveChangesAsync(ct);
        return new GeofenceImportResult(inserted, updated, matched);
    }

    public static async Task ProcessTelemetryAsync(TmsDbContext db, IReadOnlyCollection<DotTelemetryRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return;
        await EnsureSchemaAsync(db, ct);
        var geofences = await db.SiteGeofences.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        if (geofences.Count == 0) return;
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        PlannerSourceMasterDataResolver? siteResolver = null;
        try
        {
            siteResolver = await PlannerSourceMasterDataResolver.CreateAsync(db, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        foreach (var record in records.OrderBy(x => x.EventTimeUtc))
        {
            if (record.Latitude is null || record.Longitude is null) continue;
            try
            {
            var vehicle = vehicles.FirstOrDefault(v => VehicleMatches(v, record.VehicleIdentifier));
            var openVisit = await db.GeofenceVisits.OrderByDescending(x => x.EnteredAtUtc)
                .FirstOrDefaultAsync(x => x.VehicleIdentifier == record.VehicleIdentifier && x.ExitedAtUtc == null, ct);
            var inside = geofences.FirstOrDefault(g => Contains(g.PolygonJson, record.Longitude.Value, record.Latitude.Value));
            var load = await ResolveLoadAsync(db, vehicle?.Id, openVisit?.LoadId, record.EventTimeUtc, inside, siteResolver, ct);

            if (inside is not null)
            {
                if (openVisit is not null && openVisit.GeofenceId != inside.Id)
                    await CloseVisitAsync(db, openVisit, record.EventTimeUtc, "Departed", "Vehicle entered a different geofence.", ct);

                openVisit = await db.GeofenceVisits.OrderByDescending(x => x.EnteredAtUtc)
                    .FirstOrDefaultAsync(x => x.VehicleIdentifier == record.VehicleIdentifier && x.ExitedAtUtc == null && x.GeofenceId == inside.Id, ct);
                if (openVisit is null)
                {
                    var stop = MatchNextStop(load, inside, await CompletedStopIds(db, load?.Id, ct), siteResolver);
                    openVisit = new GeofenceVisit
                    {
                        GeofenceId = inside.Id, SiteId = inside.SiteId, LoadId = load?.Id, LoadStopId = stop?.Id, VehicleId = vehicle?.Id,
                        VehicleIdentifier = record.VehicleIdentifier, EnteredAtUtc = record.EventTimeUtc,
                        LastInsideAtUtc = record.EventTimeUtc, Status = "Arrived", StatusReason = stop is null
                            ? $"Entered {inside.Name}; no remaining run stop could be matched safely."
                            : $"Entered {inside.Name}; matched to stop {stop.Name}."
                    };
                    db.GeofenceVisits.Add(openVisit);
                    await LinkCanonicalRunStopAsync(db, openVisit, record.EventTimeUtc, ct);
                }
                else
                {
                    if (openVisit.LoadId is null && load is not null)
                    {
                        openVisit.LoadId = load.Id;
                        openVisit.LoadStopId ??= MatchNextStop(load, inside, await CompletedStopIds(db, load.Id, ct), siteResolver)?.Id;
                    }
                    openVisit.LastInsideAtUtc = record.EventTimeUtc;
                    openVisit.DwellMinutes = Math.Max(0, (int)Math.Floor((record.EventTimeUtc - openVisit.EnteredAtUtc).TotalMinutes));
                    var confirmMinutes = Math.Max(DefaultConfirmDwellMinutes, inside.PendingEntryMinutes);
                    if (openVisit.ConfirmedAtUtc is null && openVisit.DwellMinutes >= confirmMinutes)
                    {
                        openVisit.ConfirmedAtUtc = record.EventTimeUtc;
                        openVisit.Status = "OnSiteConfirmed";
                        openVisit.StatusReason = $"Confirmed after {openVisit.DwellMinutes} minutes in {inside.Name}.";
                        if (load is not null && load.Status is LoadStatus.Dispatched or LoadStatus.Planned)
                        {
                            load.Status = LoadStatus.InProgress;
                            if (db.Entry(load).State == EntityState.Detached)
                                await PlanningRegisterStore.SaveLoadAsync(db, load, "RoadTech Geofence Engine", ct);
                        }
                    }
                    var waitLimit = inside.MaxWaitMinutes ?? inside.CategoryMaxWaitMinutes;
                    if (waitLimit is int limit && openVisit.DwellMinutes > limit)
                    {
                        openVisit.Status = "SiteDelay";
                        openVisit.StatusReason = $"Dwell is {openVisit.DwellMinutes} minutes; site threshold is {limit} minutes.";
                    }
                    openVisit.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await LinkCanonicalRunStopAsync(db, openVisit, record.EventTimeUtc, ct);
                }
            }
            else if (openVisit is not null)
            {
                var fence = geofences.FirstOrDefault(x => x.Id == openVisit.GeofenceId);
                var exitMinutes = fence?.PendingExitMinutes ?? 0;
                if (record.EventTimeUtc - openVisit.LastInsideAtUtc >= TimeSpan.FromMinutes(exitMinutes))
                {
                    var confirmed = openVisit.ConfirmedAtUtc is not null;
                    await CloseVisitAsync(db, openVisit, record.EventTimeUtc,
                        confirmed ? "Departed" : "PassThrough",
                        confirmed ? "Confirmed site visit completed on geofence exit." : "Vehicle left before the minimum dwell confirmation.", ct);
                    if (confirmed && openVisit.LoadId is Guid visitLoadId)
                    {
                        var visitLoad = load?.Id == visitLoadId ? load : await ResolveLoadAsync(db, vehicle?.Id, visitLoadId, record.EventTimeUtc, null, siteResolver, ct);
                        db.DriverStatusLogs.Add(new DriverStatusLog
                        {
                            LoadId = visitLoadId, DriverId = visitLoad?.DriverId, Status = "GeofenceStopCompleted",
                            Notes = $"Stop {openVisit.LoadStopId} completed from geofence exit at {record.EventTimeUtc:u}.", CapturedBy = "RoadTech Geofence Engine"
                        });

                        if (visitLoad is not null)
                        {
                            var completedStopIds = await CompletedStopIds(db, visitLoad.Id, ct);
                            if (RunCompletionEvidence.CanAutoComplete(visitLoad, completedStopIds))
                            {
                                visitLoad.Status = LoadStatus.Completed;
                                db.DriverStatusLogs.Add(new DriverStatusLog
                                {
                                    LoadId = visitLoad.Id,
                                    DriverId = visitLoad.DriverId,
                                    Status = "RunCompleted",
                                    Notes = $"All {visitLoad.Stops.Count} planned stops have confirmed geofence departures; run completed at {record.EventTimeUtc:u}.",
                                    CapturedBy = "RoadTech Geofence Engine"
                                });
                                if (db.Entry(visitLoad).State == EntityState.Detached)
                                    await PlanningRegisterStore.SaveLoadAsync(db, visitLoad, "RoadTech Geofence Engine", ct);
                            }
                        }
                    }
                }
            }
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            } // end per-record try
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                // A single bad telemetry record must not abort the whole batch.
                // Clear tracked entities so the next record starts from a clean state.
                db.ChangeTracker.Clear();
                Console.Error.WriteLine($"Geofence telemetry processing failed for vehicle {record.VehicleIdentifier} at {record.EventTimeUtc:u}: {exception.Message}");
            }
        }
    }

    private static async Task<Load?> ResolveLoadAsync(
        TmsDbContext db,
        Guid? vehicleId,
        Guid? preferredLoadId,
        DateTimeOffset eventTimeUtc,
        SiteGeofence? geofence,
        PlannerSourceMasterDataResolver? siteResolver,
        CancellationToken ct)
    {
        if (preferredLoadId is Guid preferred)
        {
            try
            {
                var dedicated = await db.Loads.Include(x => x.Stops).SingleOrDefaultAsync(x => x.Id == preferred, ct);
                if (dedicated is not null) return dedicated;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
            }

            try
            {
                var registered = await PlanningRegisterStore.GetLoadAsync(db, preferred, ct);
                if (registered is not null) return registered;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
            }
        }

        if (vehicleId is null) return null;
        var planningDate = UkOperatingDate(eventTimeUtc);
        var candidates = new List<Load>();

        try
        {
            candidates.AddRange(await db.Loads.Include(x => x.Stops)
                .Where(x => x.PlanningDate == planningDate && x.VehicleId == vehicleId && x.Status != LoadStatus.Completed && x.Status != LoadStatus.Cancelled)
                .ToListAsync(ct));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        try
        {
            var registered = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
                .Where(x => x.VehicleId == vehicleId && x.Status != LoadStatus.Completed && x.Status != LoadStatus.Cancelled);
            foreach (var load in registered)
                if (candidates.All(existing => existing.Id != load.Id)) candidates.Add(load);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        var candidateIds = candidates.Select(x => x.Id).ToList();
        var completedByLoad = new Dictionary<Guid, HashSet<Guid>>();
        try
        {
            var completed = await db.GeofenceVisits.AsNoTracking()
                .Where(x => x.LoadId != null && candidateIds.Contains(x.LoadId.Value) && x.LoadStopId != null && x.ConfirmedAtUtc != null && x.ExitedAtUtc != null && x.Status == "Departed")
                .Select(x => new { LoadId = x.LoadId!.Value, StopId = x.LoadStopId!.Value })
                .ToListAsync(ct);
            completedByLoad = completed.GroupBy(x => x.LoadId).ToDictionary(group => group.Key, group => group.Select(x => x.StopId).ToHashSet());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        return candidates
            .Select(load =>
            {
                var completed = completedByLoad.GetValueOrDefault(load.Id) ?? [];
                var remaining = (load.Stops ?? []).Where(stop => !completed.Contains(stop.Id)).OrderBy(stop => stop.Sequence).ToList();
                var siteMatch = geofence is not null && remaining.Any(stop => StopMatchesGeofence(stop, geofence, siteResolver));
                var planned = remaining.Where(stop => stop.PlannedArrivalUtc != null).Select(stop => stop.PlannedArrivalUtc!.Value).FirstOrDefault();
                var distance = planned == default ? double.MaxValue : Math.Abs((planned - eventTimeUtc).TotalMinutes);
                return new { load, siteMatch, priority = LoadPriority(load.Status), distance };
            })
            .OrderByDescending(x => x.siteMatch)
            .ThenByDescending(x => x.priority)
            .ThenBy(x => x.distance)
            .ThenBy(x => x.load.Reference)
            .Select(x => x.load)
            .First();
    }

    private static async Task<HashSet<Guid>> CompletedStopIds(TmsDbContext db, Guid? loadId, CancellationToken ct)
    {
        if (loadId is null) return [];
        return (await db.GeofenceVisits.AsNoTracking().Where(x => x.LoadId == loadId && x.LoadStopId != null && x.ExitedAtUtc != null && x.ConfirmedAtUtc != null && x.Status == "Departed")
            .Select(x => x.LoadStopId!.Value).ToListAsync(ct)).ToHashSet();
    }

    private static LoadStop? MatchNextStop(Load? load, SiteGeofence geofence, HashSet<Guid> completed, PlannerSourceMasterDataResolver? siteResolver)
    {
        var remaining = load?.Stops.Where(x => !completed.Contains(x.Id)).OrderBy(x => x.Sequence).ToList() ?? [];
        var matched = remaining.FirstOrDefault(x => StopMatchesGeofence(x, geofence, siteResolver));
        return matched ?? (remaining.Count == 1 ? remaining[0] : null);
    }

    private static bool StopMatchesGeofence(LoadStop stop, SiteGeofence geofence, PlannerSourceMasterDataResolver? siteResolver)
    {
        if (siteResolver is not null && (geofence.SiteId is not null || !string.IsNullOrWhiteSpace(geofence.SiteNumber)))
        {
            var resolution = siteResolver.Resolve(stop.Name);
            if (resolution.SiteMatched)
            {
                if (geofence.SiteId is Guid siteId) return resolution.SiteId == siteId;
                if (!string.IsNullOrWhiteSpace(geofence.SiteNumber)) return Normalize(resolution.SiteNumber) == Normalize(geofence.SiteNumber);
            }
            return false;
        }

        return NamesOverlap(stop.Name, geofence.Name) || NamesOverlap(stop.Address, geofence.Name);
    }

    private static async Task CloseVisitAsync(TmsDbContext db, GeofenceVisit visit, DateTimeOffset at, string status, string reason, CancellationToken ct)
    {
        if (db.Entry(visit).State == EntityState.Detached) db.GeofenceVisits.Attach(visit);
        visit.ExitedAtUtc = at;
        visit.DwellMinutes = Math.Max(0, (int)Math.Floor((at - visit.EnteredAtUtc).TotalMinutes));
        visit.Status = status; visit.StatusReason = reason; visit.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await LinkCanonicalRunStopAsync(db, visit, at, ct);
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }

    private static async Task LinkCanonicalRunStopAsync(TmsDbContext db, GeofenceVisit visit, DateTimeOffset observedAtUtc, CancellationToken ct)
    {
        if (visit.SiteId is not Guid siteId || visit.VehicleId is not Guid vehicleId) return;

        RunStop? stop = null;
        if (visit.RunStopId is Guid existingStopId)
            stop = await db.RunStops.SingleOrDefaultAsync(row => row.RunStopId == existingStopId, ct);

        if (stop is null)
        {
            var day = UkOperatingDate(observedAtUtc);
            stop = await db.RunStops
                .Include(row => row.Run)
                .ThenInclude(run => run.ResourceAllocation)
                .Where(row => row.SiteId == siteId
                    && row.Run.PlanningDate == day
                    && row.Run.Status != RunStatus.Completed
                    && row.Run.Status != RunStatus.Cancelled
                    && row.Run.ResourceAllocation != null
                    && row.Run.ResourceAllocation.VehicleId == vehicleId
                    && row.GeofenceVisitId == null)
                .OrderBy(row => row.Sequence)
                .FirstOrDefaultAsync(ct);
        }

        if (stop is null) return;

        visit.RunId = stop.RunId;
        visit.RunStopId = stop.RunStopId;
        stop.GeofenceVisitId ??= visit.Id;
        if (stop.ActualArrival is null || visit.EnteredAtUtc < stop.ActualArrival)
            stop.ActualArrival = visit.EnteredAtUtc;
        if (visit.ExitedAtUtc is DateTimeOffset exitedAt && (stop.ActualDeparture is null || exitedAt > stop.ActualDeparture))
            stop.ActualDeparture = exitedAt;
    }

    private static Site? MatchSite(string geofenceName, IReadOnlyCollection<Site> sites)
    {
        var key = Normalize(geofenceName);
        return sites.FirstOrDefault(x => Normalize(x.Name) == key || Normalize(x.DriverTextName) == key)
            ?? sites.FirstOrDefault(x => key.Contains(Normalize(x.Name), StringComparison.Ordinal) || Normalize(x.Name).Contains(key, StringComparison.Ordinal));
    }

    private static bool VehicleMatches(Vehicle vehicle, string identifier)
    {
        var key = Normalize(identifier);
        return new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => Normalize(x) == key);
    }

    private static bool NamesOverlap(string? a, string? b)
    {
        var left = Normalize(a); var right = Normalize(b);
        return left == right || left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal);
    }

    private static bool Contains(string polygonJson, decimal longitude, decimal latitude)
    {
        using var doc = JsonDocument.Parse(polygonJson);
        var points = doc.RootElement.EnumerateArray().Select(ReadPoint).Where(point => point is not null).Select(point => point!.Value).ToArray();
        if (points.Length < 3) return false;
        var x = (double)longitude; var y = (double)latitude; var inside = false;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
        {
            var pi = points[i]; var pj = points[j];
            if (((pi.Y > y) != (pj.Y > y)) && x < (pj.X - pi.X) * (y - pi.Y) / (pj.Y - pi.Y) + pi.X) inside = !inside;
        }
        return inside;
    }

    private static (double X, double Y)? ReadPoint(JsonElement point)
    {
        if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2 && point[0].TryGetDouble(out var x) && point[1].TryGetDouble(out var y))
            return (x, y);
        if (point.ValueKind != JsonValueKind.Object) return null;
        var longitude = Double(point, "longitude") ?? Double(point, "lng") ?? Double(point, "lon") ?? Double(point, "x");
        var latitude = Double(point, "latitude") ?? Double(point, "lat") ?? Double(point, "y");
        return longitude is not null && latitude is not null ? (longitude.Value, latitude.Value) : null;
    }

    private static int LoadPriority(LoadStatus status) => status switch
    {
        LoadStatus.InProgress => 4,
        LoadStatus.Dispatched => 3,
        LoadStatus.Planned => 2,
        LoadStatus.Draft => 1,
        _ => 0
    };

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }

    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? v.ToString() : null;
    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
    private static double? Double(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetDouble(out var n) ? n : null;
}

public sealed record GeofenceImportResult(int Inserted, int Updated, int SiteMatched);
