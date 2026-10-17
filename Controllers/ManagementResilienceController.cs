using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Lightweight, resilient management summary used when optional pilot datasets
/// (geofences/commercial enrichment) are not yet available. The primary
/// ManagementController remains the richer source; this endpoint ensures the
/// management screen can still show core Orders/Runs/Fleet KPIs instead of
/// failing as a whole because one optional dataset is unavailable.
/// </summary>
[ApiController, Route("api/v1/management/resilient-summary")]
[Authorize]
public sealed class ManagementResilienceController(
    TmsDbContext db,
    ILogger<ManagementResilienceController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var last = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var first = from ?? last.AddDays(-6);
        if (first > last) return BadRequest(new { message = "'from' must be on or before 'to'." });
        if (last.DayNumber - first.DayNumber > 366) return BadRequest(new { message = "Management reports are limited to 366 days per request." });

        var warnings = new List<string>();
        var loads = await db.Loads.AsNoTracking().Include(x => x.Stops)
            .Where(x => x.PlanningDate >= first && x.PlanningDate <= last && x.Status != LoadStatus.Cancelled)
            .OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference)
            .Take(5000)
            .ToListAsync(ct);

        try { await LoadCommercialStore.EnrichAsync(db, loads, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Management commercial enrichment unavailable; core KPI response will continue.");
            warnings.Add("Commercial/load-utilisation enrichment is temporarily unavailable.");
            db.ChangeTracker.Clear();
        }

        var linkedOrderIds = loads.SelectMany(x => x.Stops).Where(x => x.OrderId != null).Select(x => x.OrderId!.Value).Distinct().ToList();
        var orders = await db.TransportOrders.AsNoTracking()
            .Where(x => linkedOrderIds.Contains(x.Id) ||
                (x.CollectionDate >= first && x.CollectionDate <= last) ||
                (x.DeliveryDate != null && x.DeliveryDate >= first && x.DeliveryDate <= last))
            .Take(10000)
            .ToListAsync(ct);

        var stops = loads.SelectMany(x => x.Stops).ToList();
        var activeVehicleCount = await db.Vehicles.AsNoTracking().CountAsync(x => x.Active, ct);
        var activeDriverCount = await db.Drivers.AsNoTracking().CountAsync(x => x.Active, ct);
        var allocatedRuns = loads.Count(x => x.DriverId != null && x.VehicleId != null);
        var assignedVehicles = loads.Where(x => x.VehicleId != null).Select(x => x.VehicleId!.Value).Distinct().Count();
        var assignedDrivers = loads.Where(x => x.DriverId != null).Select(x => x.DriverId!.Value).Distinct().Count();
        var completedRuns = loads.Count(x => x.Status == LoadStatus.Completed);

        var utilisationLoads = loads.Where(x => x.TotalPalletSpaces > 0 && x.PalletSpacesUsed != null).ToList();
        var usedSpaces = utilisationLoads.Sum(x => x.PalletSpacesUsed ?? 0);
        var availableSpaces = utilisationLoads.Sum(x => x.TotalPalletSpaces ?? 0);
        var totalMiles = loads.Sum(x => x.EstimatedDistanceMiles ?? 0);
        var emptyMiles = loads.Sum(x => x.EmptyMiles ?? 0);

        var attention = loads.Count(load => load.DriverId is null || load.VehicleId is null || load.Stops.Any(stop => stop.Latitude is null || stop.Longitude is null));

        // Geofence evidence is deliberately optional here. It enriches the
        // response when healthy but never prevents the core management view.
        var completedStopIds = new HashSet<Guid>();
        var measuredDeliveries = 0;
        var onTimeDeliveries = 0;
        double? averageDwell = null;
        var siteDelays = 0;
        var passThroughs = 0;
        var siteRows = new List<SitePerformance>();
        try
        {
            await GeofenceRunProgression.EnsureSchemaAsync(db, ct);
            var loadIds = loads.Select(x => x.Id).ToList();
            var visits = loadIds.Count == 0
                ? new List<GeofenceVisit>()
                : await db.GeofenceVisits.AsNoTracking()
                    .Where(x => x.LoadId != null && loadIds.Contains(x.LoadId.Value))
                    .OrderBy(x => x.EnteredAtUtc)
                    .Take(20000)
                    .ToListAsync(ct);
            var geofenceIds = visits.Select(x => x.GeofenceId).Distinct().ToList();
            var fences = geofenceIds.Count == 0
                ? new Dictionary<Guid, SiteGeofence>()
                : await db.SiteGeofences.AsNoTracking().Where(x => geofenceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var ordersById = orders.ToDictionary(x => x.Id);
            var stopsById = stops.ToDictionary(x => x.Id);
            var confirmed = visits.Where(x => x.ConfirmedAtUtc != null).ToList();
            var departed = confirmed.Where(x => x.ExitedAtUtc != null).ToList();
            completedStopIds = departed.Where(x => x.LoadStopId != null && x.Status == "Departed").Select(x => x.LoadStopId!.Value).ToHashSet();
            averageDwell = departed.Count == 0 ? null : Math.Round(departed.Average(x => x.DwellMinutes), 1);
            siteDelays = visits.Count(x => x.Status == "SiteDelay" || IsVisitOverLimit(x, fences));
            passThroughs = visits.Count(x => x.Status == "PassThrough");

            foreach (var visit in confirmed)
            {
                if (visit.LoadStopId is not Guid stopId || !stopsById.TryGetValue(stopId, out var stop) || stop.OrderId is not Guid orderId || !ordersById.TryGetValue(orderId, out var order) || order.DeliveryWindowEndUtc is null)
                    continue;
                measuredDeliveries++;
                if (visit.ConfirmedAtUtc <= order.DeliveryWindowEndUtc && (order.DeliveryWindowStartUtc is null || visit.ConfirmedAtUtc >= order.DeliveryWindowStartUtc)) onTimeDeliveries++;
            }

            siteRows = confirmed.GroupBy(x => x.GeofenceId).Select(group =>
            {
                fences.TryGetValue(group.Key, out var fence);
                var departedAtSite = group.Where(x => x.ExitedAtUtc != null).ToList();
                var delays = group.Count(x => x.Status == "SiteDelay" || IsVisitOverLimit(x, fences));
                return new SitePerformance(
                    fence?.Name ?? "Unmatched geofence",
                    fence?.Category,
                    group.Count(),
                    departedAtSite.Count == 0 ? null : Math.Round(departedAtSite.Average(x => x.DwellMinutes), 1),
                    departedAtSite.Count == 0 ? null : departedAtSite.Max(x => x.DwellMinutes),
                    fence?.MaxWaitMinutes ?? fence?.CategoryMaxWaitMinutes,
                    delays,
                    Percent(delays, group.Count()));
            }).OrderByDescending(x => x.Delays).ThenByDescending(x => x.AverageDwellMinutes).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Management geofence evidence unavailable; core KPI response will continue.");
            warnings.Add("Geofence service/dwell evidence is temporarily unavailable; core planning KPIs are still shown.");
            db.ChangeTracker.Clear();
        }

        var customerRows = orders.GroupBy(x => x.CustomerCode).Select(group => new CustomerPerformance(
            group.Key,
            group.Count(),
            0,
            0,
            0,
            null,
            null,
            0)).OrderByDescending(x => x.Orders).ThenBy(x => x.CustomerCode).ToList();

        var days = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(offset =>
        {
            var date = first.AddDays(offset);
            var dayLoads = loads.Where(x => x.PlanningDate == date).ToList();
            return new DailyPerformance(date, dayLoads.Count, dayLoads.Count(x => x.Status == LoadStatus.Completed), 0, 0, null, null, 0);
        }).ToList();

        return Ok(new
        {
            from = first,
            to = last,
            generatedAtUtc = DateTimeOffset.UtcNow,
            degraded = warnings.Count > 0,
            warnings,
            headline = new HeadlineMetrics(
                orders.Count,
                loads.Count,
                completedRuns,
                Percent(completedRuns, loads.Count),
                stops.Count,
                completedStopIds.Count,
                Percent(completedStopIds.Count, stops.Count),
                measuredDeliveries,
                onTimeDeliveries,
                Percent(onTimeDeliveries, measuredDeliveries),
                averageDwell,
                siteDelays,
                passThroughs,
                attention,
                Percent(attention, loads.Count)),
            efficiency = new EfficiencyMetrics(
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
            etaPrecision = new EtaPrecisionMetrics(false, 0, null, null, null, null, "ETA precision loads separately from the persisted pilot snapshot feed."),
            customers = customerRows,
            sites = siteRows,
            days
        });
    }

    private static bool IsVisitOverLimit(GeofenceVisit visit, IReadOnlyDictionary<Guid, SiteGeofence> fences)
    {
        if (!fences.TryGetValue(visit.GeofenceId, out var fence)) return false;
        var limit = fence.MaxWaitMinutes ?? fence.CategoryMaxWaitMinutes;
        return limit is int minutes && visit.DwellMinutes > minutes;
    }

    private static decimal? Percent(int value, int total) => total <= 0 ? null : Math.Round((decimal)value / total * 100m, 1);
}
