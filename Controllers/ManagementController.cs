using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/management")]
[Authorize]
public sealed class ManagementController(TmsDbContext db) : ControllerBase
{
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var last = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var first = from ?? last.AddDays(-6);
        if (first > last) return BadRequest("'from' must be on or before 'to'.");
        if (last.DayNumber - first.DayNumber > 366) return BadRequest("Management reports are limited to 366 days per request.");

        await GeofenceRunProgression.EnsureSchemaAsync(db, ct);

        var loads = await db.Loads.AsNoTracking().Include(x => x.Stops)
            .Where(x => x.PlanningDate >= first && x.PlanningDate <= last && x.Status != LoadStatus.Cancelled)
            .OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference)
            .Take(5000)
            .ToListAsync(ct);
        await LoadCommercialStore.EnrichAsync(db, loads, ct);

        var loadIds = loads.Select(x => x.Id).ToList();
        var linkedOrderIds = loads.SelectMany(x => x.Stops).Where(x => x.OrderId != null).Select(x => x.OrderId!.Value).Distinct().ToList();
        var orders = await db.TransportOrders.AsNoTracking()
            .Where(x => linkedOrderIds.Contains(x.Id) ||
                (x.CollectionDate >= first && x.CollectionDate <= last) ||
                (x.DeliveryDate != null && x.DeliveryDate >= first && x.DeliveryDate <= last))
            .Take(10000)
            .ToListAsync(ct);
        var ordersById = orders.ToDictionary(x => x.Id);
        var stops = loads.SelectMany(x => x.Stops).ToList();
        var stopsById = stops.ToDictionary(x => x.Id);

        var visits = loadIds.Count == 0
            ? []
            : await db.GeofenceVisits.AsNoTracking()
                .Where(x => x.LoadId != null && loadIds.Contains(x.LoadId.Value))
                .OrderBy(x => x.EnteredAtUtc)
                .Take(20000)
                .ToListAsync(ct);
        var geofenceIds = visits.Select(x => x.GeofenceId).Distinct().ToList();
        var fences = geofenceIds.Count == 0
            ? new Dictionary<Guid, SiteGeofence>()
            : await db.SiteGeofences.AsNoTracking().Where(x => geofenceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        var routineStatuses = new[] { "Dispatched", "Accepted", "GeofenceStopCompleted" };
        var statusLogs = loadIds.Count == 0
            ? []
            : await db.DriverStatusLogs.AsNoTracking()
                .Where(x => loadIds.Contains(x.LoadId))
                .OrderBy(x => x.CapturedAtUtc)
                .Take(20000)
                .ToListAsync(ct);

        var activeVehicleCount = await db.Vehicles.AsNoTracking().CountAsync(x => x.Active, ct);
        var activeDriverCount = await db.Drivers.AsNoTracking().CountAsync(x => x.Active, ct);

        var confirmedVisits = visits.Where(x => x.ConfirmedAtUtc != null).ToList();
        var departedVisits = confirmedVisits.Where(x => x.ExitedAtUtc != null).ToList();
        var passThroughs = visits.Count(x => x.Status == "PassThrough");
        var siteDelays = visits.Count(x => x.Status == "SiteDelay" || IsVisitOverLimit(x, fences));

        var serviceVisits = confirmedVisits.Select(visit =>
        {
            stopsById.TryGetValue(visit.LoadStopId ?? Guid.Empty, out var stop);
            var order = stop?.OrderId is Guid orderId && ordersById.TryGetValue(orderId, out var matched) ? matched : null;
            return new ServiceVisit(visit, stop, order, IsOnTime(visit.ConfirmedAtUtc, order));
        }).ToList();
        var measuredService = serviceVisits.Where(x => x.Order?.DeliveryWindowEndUtc != null).ToList();
        var onTimeCount = measuredService.Count(x => x.OnTime == true);

        var plannedStops = stops.Count;
        var completedStops = visits.Where(x => x.LoadStopId != null && x.ConfirmedAtUtc != null && x.ExitedAtUtc != null && x.Status == "Departed")
            .Select(x => x.LoadStopId!.Value).Distinct().Count();
        var completedRuns = loads.Count(x => x.Status == LoadStatus.Completed || RunFullyCompleted(x, visits));
        var allocatedRuns = loads.Count(x => x.DriverId != null && x.VehicleId != null);
        var assignedVehicles = loads.Where(x => x.VehicleId != null).Select(x => x.VehicleId!.Value).Distinct().Count();
        var assignedDrivers = loads.Where(x => x.DriverId != null).Select(x => x.DriverId!.Value).Distinct().Count();

        var utilisationLoads = loads.Where(x => x.TotalPalletSpaces > 0 && x.PalletSpacesUsed != null).ToList();
        var usedSpaces = utilisationLoads.Sum(x => x.PalletSpacesUsed ?? 0);
        var availableSpaces = utilisationLoads.Sum(x => x.TotalPalletSpaces ?? 0);
        var totalMiles = loads.Sum(x => x.EstimatedDistanceMiles ?? 0);
        var emptyMiles = loads.Sum(x => x.EmptyMiles ?? 0);

        var attentionLoadIds = new HashSet<Guid>();
        foreach (var load in loads)
        {
            if (load.DriverId is null || load.VehicleId is null || load.Stops.Any(x => x.Latitude is null || x.Longitude is null))
                attentionLoadIds.Add(load.Id);
        }
        foreach (var visit in visits.Where(x => x.LoadId != null && (x.Status is "SiteDelay" or "PassThrough" || IsVisitOverLimit(x, fences))))
            attentionLoadIds.Add(visit.LoadId!.Value);
        foreach (var log in statusLogs.Where(x => !routineStatuses.Contains(x.Status, StringComparer.OrdinalIgnoreCase)))
            attentionLoadIds.Add(log.LoadId);

        var customerRows = serviceVisits
            .Where(x => x.Order is not null)
            .GroupBy(x => x.Order!.CustomerCode)
            .Select(group =>
            {
                var measured = group.Where(x => x.Order!.DeliveryWindowEndUtc != null).ToList();
                var dwell = group.Where(x => x.Visit.ExitedAtUtc != null).Select(x => x.Visit.DwellMinutes).ToList();
                return new CustomerPerformance(
                    group.Key,
                    group.Select(x => x.Order!.Id).Distinct().Count(),
                    group.Count(),
                    measured.Count,
                    measured.Count(x => x.OnTime == true),
                    Percent(measured.Count(x => x.OnTime == true), measured.Count),
                    dwell.Count == 0 ? null : Math.Round(dwell.Average(), 1),
                    group.Count(x => x.Visit.Status == "SiteDelay" || IsVisitOverLimit(x.Visit, fences)));
            })
            .OrderByDescending(x => x.Deliveries)
            .ThenBy(x => x.CustomerCode)
            .ToList();

        var siteRows = confirmedVisits
            .GroupBy(x => x.GeofenceId)
            .Select(group =>
            {
                fences.TryGetValue(group.Key, out var fence);
                var completed = group.Where(x => x.ExitedAtUtc != null).ToList();
                var delays = group.Count(x => x.Status == "SiteDelay" || IsVisitOverLimit(x, fences));
                return new SitePerformance(
                    fence?.Name ?? "Unmatched geofence",
                    fence?.Category,
                    group.Count(),
                    completed.Count == 0 ? null : Math.Round(completed.Average(x => x.DwellMinutes), 1),
                    completed.Count == 0 ? null : completed.Max(x => x.DwellMinutes),
                    fence?.MaxWaitMinutes ?? fence?.CategoryMaxWaitMinutes,
                    delays,
                    Percent(delays, group.Count()));
            })
            .OrderByDescending(x => x.Delays)
            .ThenByDescending(x => x.AverageDwellMinutes)
            .ThenBy(x => x.Site)
            .ToList();

        var days = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(offset =>
        {
            var date = first.AddDays(offset);
            var dayLoads = loads.Where(x => x.PlanningDate == date).ToList();
            var dayLoadIds = dayLoads.Select(x => x.Id).ToHashSet();
            var dayVisits = serviceVisits.Where(x => x.Visit.LoadId != null && dayLoadIds.Contains(x.Visit.LoadId.Value)).ToList();
            var measured = dayVisits.Where(x => x.Order?.DeliveryWindowEndUtc != null).ToList();
            var dwell = dayVisits.Where(x => x.Visit.ExitedAtUtc != null).Select(x => x.Visit.DwellMinutes).ToList();
            return new DailyPerformance(
                date,
                dayLoads.Count,
                dayLoads.Count(x => x.Status == LoadStatus.Completed || RunFullyCompleted(x, visits)),
                dayVisits.Count,
                measured.Count,
                Percent(measured.Count(x => x.OnTime == true), measured.Count),
                dwell.Count == 0 ? null : Math.Round(dwell.Average(), 1),
                dayVisits.Count(x => x.Visit.Status == "SiteDelay" || IsVisitOverLimit(x.Visit, fences)));
        }).ToList();

        var response = new ManagementSummary(
            first,
            last,
            DateTimeOffset.UtcNow,
            new HeadlineMetrics(
                orders.Count,
                loads.Count,
                completedRuns,
                Percent(completedRuns, loads.Count),
                plannedStops,
                completedStops,
                Percent(completedStops, plannedStops),
                measuredService.Count,
                onTimeCount,
                Percent(onTimeCount, measuredService.Count),
                departedVisits.Count == 0 ? null : Math.Round(departedVisits.Average(x => x.DwellMinutes), 1),
                siteDelays,
                passThroughs,
                attentionLoadIds.Count,
                Percent(attentionLoadIds.Count, loads.Count)),
            new EfficiencyMetrics(
                allocatedRuns,
                Percent(allocatedRuns, loads.Count),
                assignedVehicles,
                activeVehicleCount,
                Percent(assignedVehicles, activeVehicleCount),
                assignedDrivers,
                activeDriverCount,
                Percent(assignedDrivers, activeDriverCount),
                availableSpaces > 0 ? Math.Round(usedSpaces / availableSpaces * 100m, 1) : null,
                totalMiles,
                emptyMiles,
                totalMiles > 0 ? Math.Round(emptyMiles / totalMiles * 100m, 1) : null),
            new EtaPrecisionMetrics(false, 0, null, null, null, null,
                "Historical ETA snapshots are not persisted yet. The live ETA remains available operationally; precision will populate once snapshot capture is enabled during the pilot."),
            customerRows,
            siteRows,
            days);

        return Ok(response);
    }

    private static bool RunFullyCompleted(Load load, IReadOnlyCollection<GeofenceVisit> visits)
    {
        if (load.Stops.Count == 0) return false;
        var completed = visits.Where(x => x.LoadId == load.Id && x.LoadStopId != null && x.ConfirmedAtUtc != null && x.ExitedAtUtc != null && x.Status == "Departed")
            .Select(x => x.LoadStopId!.Value).ToHashSet();
        return load.Stops.All(x => completed.Contains(x.Id));
    }

    private static bool? IsOnTime(DateTimeOffset? actual, TransportOrder? order)
    {
        if (actual is null || order?.DeliveryWindowEndUtc is null) return null;
        if (actual > order.DeliveryWindowEndUtc) return false;
        if (order.DeliveryWindowStartUtc is DateTimeOffset start && actual < start) return false;
        return true;
    }

    private static bool IsVisitOverLimit(GeofenceVisit visit, IReadOnlyDictionary<Guid, SiteGeofence> fences)
    {
        if (!fences.TryGetValue(visit.GeofenceId, out var fence)) return false;
        var limit = fence.MaxWaitMinutes ?? fence.CategoryMaxWaitMinutes;
        return limit is int minutes && visit.DwellMinutes > minutes;
    }

    private static decimal? Percent(int value, int total) => total <= 0 ? null : Math.Round((decimal)value / total * 100m, 1);

    private sealed record ServiceVisit(GeofenceVisit Visit, LoadStop? Stop, TransportOrder? Order, bool? OnTime);
}

public sealed record ManagementSummary(
    DateOnly From,
    DateOnly To,
    DateTimeOffset GeneratedAtUtc,
    HeadlineMetrics Headline,
    EfficiencyMetrics Efficiency,
    EtaPrecisionMetrics EtaPrecision,
    IReadOnlyList<CustomerPerformance> Customers,
    IReadOnlyList<SitePerformance> Sites,
    IReadOnlyList<DailyPerformance> Days);

public sealed record HeadlineMetrics(
    int Orders,
    int Runs,
    int CompletedRuns,
    decimal? RunCompletionPercent,
    int PlannedStops,
    int CompletedStops,
    decimal? StopCompletionPercent,
    int MeasuredDeliveries,
    int OnTimeDeliveries,
    decimal? OnTimeDeliveryPercent,
    double? AverageSiteDwellMinutes,
    int SiteDelays,
    int PassThroughs,
    int AttentionRuns,
    decimal? AttentionRatePercent);

public sealed record EfficiencyMetrics(
    int AllocatedRuns,
    decimal? AllocationPercent,
    int AssignedVehicles,
    int ActiveVehicles,
    decimal? FleetUtilisationPercent,
    int AssignedDrivers,
    int ActiveDrivers,
    decimal? DriverUtilisationPercent,
    decimal? LoadUtilisationPercent,
    decimal TotalMiles,
    decimal EmptyMiles,
    decimal? EmptyMilesPercent);

public sealed record EtaPrecisionMetrics(
    bool DataAvailable,
    int Samples,
    decimal? Within10MinutesPercent,
    decimal? Within15MinutesPercent,
    decimal? Within30MinutesPercent,
    double? MeanAbsoluteErrorMinutes,
    string Message);

public sealed record CustomerPerformance(
    string CustomerCode,
    int Orders,
    int Deliveries,
    int MeasuredDeliveries,
    int OnTimeDeliveries,
    decimal? OnTimePercent,
    double? AverageDwellMinutes,
    int SiteDelays);

public sealed record SitePerformance(
    string Site,
    string? Category,
    int Visits,
    double? AverageDwellMinutes,
    int? MaximumDwellMinutes,
    int? WaitLimitMinutes,
    int Delays,
    decimal? DelayRatePercent);

public sealed record DailyPerformance(
    DateOnly Date,
    int Runs,
    int CompletedRuns,
    int Deliveries,
    int MeasuredDeliveries,
    decimal? OnTimePercent,
    double? AverageDwellMinutes,
    int SiteDelays);