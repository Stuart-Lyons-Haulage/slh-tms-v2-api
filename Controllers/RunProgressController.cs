using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/run-progress")]
[Authorize]
public sealed class RunProgressController(
    TmsDbContext db,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? date,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayKey) && Request.Query.TryGetValue("key", out var queryKey))
            displayKey = queryKey.FirstOrDefault();
        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);
        if (!pairedKeyAllowed && !TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .ToList();
        await RunOperationalStore.EnrichAsync(db, loads, ct);
        WallboardPhysicalStops.Apply(loads);

        var stopIds = loads.SelectMany(load => load.Stops ?? []).Select(stop => stop.Id).Distinct().ToList();
        var mappings = stopIds.Count == 0
            ? []
            : await db.IntegrationMappings.AsNoTracking()
                .Where(mapping => mapping.Active && mapping.Provider == "Samsara" && mapping.TmsEntityType == "LoadStop" && stopIds.Contains(mapping.TmsEntityId))
                .ToListAsync(ct);
        var progressByStop = mappings
            .Select(mapping => (mapping.TmsEntityId, Progress: SamsaraRouteProgressService.ReadProgress(mapping.Notes)))
            .Where(item => item.Progress is not null)
            .GroupBy(item => item.TmsEntityId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Progress!.OccurredAtUtc).First().Progress!);

        var records = loads.Select(load => BuildRecord(load, progressByStop, now)).ToList();
        return Ok(new
        {
            planningDate,
            calculatedAtUtc = now,
            count = records.Count,
            source = "PlanningResilience+SamsaraRouteAuditFeed",
            samsaraProgressAvailable = progressByStop.Count > 0,
            latestSamsaraEventUtc = progressByStop.Values.Select(progress => progress.OccurredAtUtc).Where(value => value is not null).Max(),
            records
        });
    }

    private static object BuildRecord(Load load, IReadOnlyDictionary<Guid, SamsaraStopProgressState> progressByStop, DateTimeOffset now)
    {
        var stops = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
        var stopRows = stops.Select(stop => new
        {
            Stop = stop,
            Progress = progressByStop.GetValueOrDefault(stop.Id)
        }).ToList();
        var completed = stopRows.Where(row => row.Progress?.DepartureTime is not null).ToList();
        var current = stopRows.FirstOrDefault(row => row.Progress?.ArrivalTime is not null && row.Progress.DepartureTime is null);
        var next = stopRows.FirstOrDefault(row => row.Progress?.DepartureTime is null);
        var routeStarted = stopRows.Any(row => row.Progress?.EnRouteTime is not null || row.Progress?.ArrivalTime is not null || row.Progress?.DepartureTime is not null);
        var allStopsComplete = stops.Count > 0 && completed.Count == stops.Count;
        var runState = allStopsComplete ? "Completed" : current is not null ? "OnSiteConfirmed" : routeStarted ? "BetweenStops" : "Planned";
        var arrival = current?.Progress?.ArrivalTime;
        var dwellSeconds = arrival is null ? (int?)null : Math.Max(0, (int)Math.Floor((now - arrival.Value).TotalSeconds));
        var completedPercent = stops.Count == 0 ? 0m : Math.Round((decimal)completed.Count / stops.Count * 100m, 1);

        return new
        {
            loadId = load.Id,
            loadReference = load.Reference,
            loadStatus = allStopsComplete ? LoadStatus.Completed.ToString() : load.Status.ToString(),
            runState,
            totalStops = stops.Count,
            completedStops = completed.Count,
            progressPercent = completedPercent,
            nextStop = next is null ? null : new { next.Stop.Id, next.Stop.Sequence, next.Stop.Name, next.Stop.Address, next.Stop.PlannedArrivalUtc, estimatedArrivalTime = next.Progress?.EstimatedArrivalTime },
            phase = allStopsComplete ? "Complete" : current is not null ? "On site" : routeStarted ? "Heading to" : "Next job",
            focusStop = current?.Stop.Name ?? next?.Stop.Name,
            trackingFresh = false,
            trackingMoving = false,
            trackingObservedAtUtc = stopRows.Select(row => row.Progress?.OccurredAtUtc).Where(value => value is not null).Max(),
            stopDwell = stopRows.Select(row => new
            {
                stopId = row.Stop.Id,
                sequence = row.Stop.Sequence,
                stopName = row.Stop.Name,
                state = row.Progress?.DepartureTime is not null ? "Completed" : row.Progress?.ArrivalTime is not null ? "OnSite" : row.Progress?.State ?? "Planned",
                siteArrivalUtc = row.Progress?.ArrivalTime,
                siteDepartureUtc = row.Progress?.DepartureTime,
                liveDwellSeconds = row.Progress?.ArrivalTime is not null && row.Progress.DepartureTime is null
                    ? Math.Max(0, (int)Math.Floor((now - row.Progress.ArrivalTime.Value).TotalSeconds))
                    : (int?)null,
                finalDwellSeconds = row.Progress?.ArrivalTime is not null && row.Progress.DepartureTime is not null
                    ? Math.Max(0, (int)Math.Floor((row.Progress.DepartureTime.Value - row.Progress.ArrivalTime.Value).TotalSeconds))
                    : (int?)null
            }),
            currentVisit = current is null || arrival is null ? null : new
            {
                siteName = current.Stop.Name,
                enteredAtUtc = arrival,
                siteArrivalUtc = arrival,
                confirmedAtUtc = arrival,
                dwellMinutes = dwellSeconds / 60,
                liveDwellMinutes = dwellSeconds / 60,
                liveDwellSeconds = dwellSeconds,
                isDelayed = false,
                status = "OnSite",
                statusReason = "Arrival reported by Samsara."
            },
            lastDeparture = stopRows.Where(row => row.Progress?.DepartureTime is not null)
                .OrderByDescending(row => row.Progress!.DepartureTime)
                .Select(row => new { siteName = row.Stop.Name, exitedAtUtc = row.Progress!.DepartureTime, siteDepartureUtc = row.Progress.DepartureTime })
                .FirstOrDefault(),
            linkageException = (object?)null,
            calculatedAtUtc = now
        };
    }

    private static DateOnly UkOperatingDate(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
        return DateOnly.FromDateTime(local.DateTime);
    }
}
