using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Register-first run timeline used by the operational Live Runs board.
/// This remains available when the legacy Loads/LoadStops schema is unavailable.
/// </summary>
[ApiController, Route("api/v1/intelligence/run-timeline")]
[Authorize]
public sealed class RunTimelineResilienceController(TmsDbContext db, TachoMasterClient tachoMaster) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var load = await PlanningResilience.ReadLoadAsync(db, id, ct);
        if (load is null) return NotFound();

        try { await LoadCommercialStore.EnrichAsync(db, [load], ct); }
        catch (Exception exception) when (exception is not OperationCanceledException) { db.ChangeTracker.Clear(); }

        var displayReference = RunDisplayLabel.For(load);
        var timelineStatus = load.Status.ToString();
        var events = new List<TimelineEvent>
        {
            new(load.CreatedAtUtc, "Run created", displayReference, "Planning", null)
        };

        try
        {
            var logs = await db.DriverStatusLogs.AsNoTracking()
                .Where(x => x.LoadId == id)
                .OrderBy(x => x.CapturedAtUtc)
                .ToListAsync(ct);
            events.AddRange(logs.Select(x => new TimelineEvent(
                x.CapturedAtUtc,
                x.Status,
                x.Notes ?? "Operational status updated",
                "Operations",
                x.CapturedBy)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        // Make the start of execution visible: planned allocation -> Tacho duty ->
        // first physical DOT/Falcon movement. This is the beginning of the same
        // evidence chain later used for site progression and customer ETA proof.
        try
        {
            if (load.VehicleId is Guid vehicleId)
            {
                var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == vehicleId, ct);
                if (vehicle is not null)
                {
                    var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(db, [vehicle], ct);
                    var aliases = aliasesByVehicle.GetValueOrDefault(vehicle.Id) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var tachoStatuses = await tachoMaster.GetCurrentDriverStatusesByVehicleAsync(load.PlanningDate, ct);
                    var tacho = ExecutionIdentityResolver.MatchTacho(aliases, tachoStatuses);
                    if (tacho is not null)
                    {
                        events.Add(new TimelineEvent(
                            tacho.DutyStartUtc,
                            "Tacho sign-on / duty start",
                            $"{tacho.DriverName} · vehicle {vehicle.Registration} · {tacho.DriveAvailableTodayMinutes?.ToString() ?? "unknown"} drive minutes available today",
                            "TachoMaster",
                            null));
                    }

                    var (startUtc, endUtc) = OperatingWindow(load.PlanningDate);
                    var tracking = await db.VehicleTrackingEvents.AsNoTracking()
                        .Where(x => x.EventTimeUtc >= startUtc.AddHours(-2) && x.EventTimeUtc < endUtc.AddHours(2))
                        .OrderBy(x => x.EventTimeUtc)
                        .Take(30000)
                        .ToListAsync(ct);
                    var firstMovement = ExecutionIdentityResolver.FirstMovement(aliases, tracking, tacho?.DutyStartUtc);
                    if (firstMovement is not null)
                    {
                        var delay = tacho is null ? (int?)null : Math.Max(0, (int)Math.Floor((firstMovement.Value - tacho.DutyStartUtc).TotalMinutes));
                        events.Add(new TimelineEvent(
                            firstMovement.Value,
                            "First DOT/Falcon movement",
                            delay is null
                                ? $"{vehicle.Registration} first movement for the operating day; no matched Tacho duty was available."
                                : $"{vehicle.Registration} moved {delay} minute{(delay == 1 ? string.Empty : "s")} after Tacho sign-on.",
                            "DOT/Falcon",
                            null));
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        var derivedTrackingAdded = false;
        try
        {
            var geofenceLoad = GeofencePlanningMatch.PrepareLoad(load);
            var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, load.PlanningDate, [geofenceLoad], ct);
            var visits = snapshot.Visits.Where(x => x.LoadId == id).OrderBy(x => x.EnteredAtUtc).ToList();
            derivedTrackingAdded = visits.Count > 0;

            foreach (var visit in visits)
            {
                events.Add(new TimelineEvent(
                    visit.EnteredAtUtc,
                    "Geofence arrival",
                    $"{visit.Fence.Name} · {visit.VehicleIdentifier}",
                    "Tracking",
                    null));
                if (visit.ConfirmedAtUtc is not null)
                    events.Add(new TimelineEvent(
                        visit.ConfirmedAtUtc.Value,
                        "Site visit confirmed",
                        $"{visit.Fence.Name} · dwell threshold confirmed · {visit.DwellMinutes} min",
                        "Tracking",
                        null));
                if (visit.ExitedAtUtc is not null)
                    events.Add(new TimelineEvent(
                        visit.ExitedAtUtc.Value,
                        "Geofence departure",
                        $"{visit.Fence.Name} · {visit.DwellMinutes} min dwell",
                        "Tracking",
                        null));
            }

            var completedStopIds = GeofencePlanningMatch.CompletedStopIds(load, visits);
            var totalStops = (load.Stops ?? []).Count;
            var active = snapshot.ActiveVisits.Any(x => x.LoadId == id);
            timelineStatus = totalStops > 0 && completedStopIds.Count >= totalStops
                ? "Completed"
                : active
                    ? "On site"
                    : completedStopIds.Count > 0
                        ? "In progress"
                        : timelineStatus;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        if (!derivedTrackingAdded)
        {
            try
            {
                var visits = await db.GeofenceVisits.AsNoTracking()
                    .Where(x => x.LoadId == id)
                    .ToListAsync(ct);
                foreach (var visit in visits)
                {
                    events.Add(new TimelineEvent(
                        visit.EnteredAtUtc,
                        "Geofence arrival",
                        visit.StatusReason ?? visit.VehicleIdentifier,
                        "Tracking",
                        null));
                    if (visit.ConfirmedAtUtc is not null)
                        events.Add(new TimelineEvent(
                            visit.ConfirmedAtUtc.Value,
                            "Site visit confirmed",
                            $"Dwell threshold confirmed · {visit.DwellMinutes} min",
                            "Tracking",
                            null));
                    if (visit.ExitedAtUtc is not null)
                        events.Add(new TimelineEvent(
                            visit.ExitedAtUtc.Value,
                            "Geofence departure",
                            visit.Status,
                            "Tracking",
                            null));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
            }
        }

        try
        {
            var changes = await PlanLockStore.ChangesAsync(db, load.PlanningDate, load.PlanningDate, ct);
            events.AddRange(changes
                .Where(x => x.LoadId == id)
                .Select(x => new TimelineEvent(
                    x.ChangedAtUtc,
                    x.ChangeType,
                    x.Reason,
                    "Plan change",
                    x.ChangedBy)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        return Ok(new
        {
            entityType = "Run",
            id = load.Id,
            reference = displayReference,
            planningDate = load.PlanningDate,
            status = timelineStatus,
            events = events.OrderBy(x => x.AtUtc)
        });
    }

    private static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) OperatingWindow(DateOnly date)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            var localStart = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            return (new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone)), new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(1), zone)));
        }
        catch (TimeZoneNotFoundException)
        {
            var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return (start, start.AddDays(1));
        }
    }

    private sealed record TimelineEvent(DateTimeOffset AtUtc, string Title, string Detail, string Source, string? By);
}
