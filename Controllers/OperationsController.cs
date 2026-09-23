using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
 
namespace Slh.Tms.Api.Controllers;
 
[ApiController, Route("api/v1/operations")]
[Authorize]
public sealed class OperationsController(
    TmsDbContext db,
    AzureMapsRouteClient maps,
    TachoMasterClient tachoMaster,
    DotTrackingClient trackingClient,
    DotTrackingTelemetryStore telemetryStore,
    SiteTimingRuleStore timingRuleStore,
    ILogger<OperationsController> logger,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("delivery-etas"), AllowAnonymous]
    public async Task<IActionResult> DeliveryEtas([FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();
 
        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        // A planning-day reset leaves cancelled rows in the legacy/live Loads table while
        // the re-imported active plan is authoritative in the planning register. Use the
        // same resilient merged reader as TV/run progression so the ETA feed cannot go
        // empty merely because a cancelled live tombstone exists for an active run.
        var loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.PlanningDate == planningDate && load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .Take(200)
            .ToList();
        await RunOperationalStore.EnrichAsync(db, loads, ct);
        WallboardPhysicalStops.Apply(loads);
        var orderIds = loads.SelectMany(load => load.Stops).Where(stop => stop.OrderId != null).Select(stop => stop.OrderId!.Value).Distinct().ToList();
        var orders = await SafeDictionary(db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)), order => order.Id, ct);
        if (orders.Count == 0 && orderIds.Count > 0) orders = (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct)).Where(order => orderIds.Contains(order.Id)).ToDictionary(order => order.Id);
        var vehicleIds = loads.Where(load => load.VehicleId != null).Select(load => load.VehicleId!.Value).Distinct().ToList();
        var driverIds = loads.Where(load => load.DriverId != null).Select(load => load.DriverId!.Value).Distinct().ToList();
        var vehicles = await SafeDictionary(db.Vehicles.AsNoTracking().Where(vehicle => vehicleIds.Contains(vehicle.Id)), vehicle => vehicle.Id, ct);
        var drivers = await SafeDictionary(
            db.Drivers
                .AsNoTracking()
                .Where(driver => driverIds.Contains(driver.Id))
                .Select(driver => new Driver
                {
                    Id = driver.Id,
                    EmployeeNumber = driver.EmployeeNumber,
                    DisplayName = driver.DisplayName,
                    TachoName = driver.TachoName,
                    TachoMasterDriverId = driver.TachoMasterDriverId,
                    TachoCardNumber = driver.TachoCardNumber,
                    Active = driver.Active
                }),
            driver => driver.Id,
            ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers.Values.ToList(), ct);
        var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(db, vehicles.Values.ToList(), ct);
        try
        {
            var trackingRecords = (await trackingClient.GetLatestVehicleEventsAsync(ct))
                .Select(DotTelemetryRecord.FromProvider)
                .Where(record => record.Latitude is not null && record.Longitude is not null)
                .ToList();
            if (trackingRecords.Count > 0)
                await telemetryStore.PersistAsync(trackingRecords, ct, markAsLiveReceipt: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "RoadTech live refresh failed while calculating delivery ETAs; using stored live telemetry fallback.");
            db.ChangeTracker.Clear();
        }
        var statuses = await SafeList(db.VehicleLiveStatuses.AsNoTracking(), ct);
        IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> tachoStatuses = new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>();
        try { tachoStatuses = await tachoMaster.GetLiveDriverStatusesByVehicleAsync(planningDate, ct); }
        catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogWarning(exception, "TachoMaster data was unavailable for tacho-aware ETA calculations."); }
 
        EmbeddedGeofenceSnapshot? geofence = null;
        try
        {
            geofence = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, GeofencePlanningMatch.PrepareLoads(loads), ct);
            // The in-memory RoadTech reconstruction can briefly be incomplete while a refresh is
            // catching up. Merge the durable projection before calculating ETAs so already-proved
            // stops cannot disappear and send the route backwards through completed work.
            geofence = await EmbeddedGeofenceEvidenceMerge.MergeDurableProjectionAsync(db, geofence, loads, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Geofence progression was unavailable while calculating delivery ETAs; live ETAs will fail closed rather than routing completed work again.");
            db.ChangeTracker.Clear();
        }
 
        var now = DateTimeOffset.UtcNow;
        var timingSites = await db.Sites.AsNoTracking().Where(site => site.Active).Take(5000).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, timingSites, ct);
        var timingRules = await timingRuleStore.ReadAsync(ct);
        var records = new List<DeliveryEtaResponse>();
 
        foreach (var load in loads)
        {
            var vehicle = load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var matchedVehicle) ? matchedVehicle : null;
            var aliases = vehicle is not null && aliasesByVehicle.TryGetValue(vehicle.Id, out var knownAliases)
                ? knownAliases
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var live = vehicle is null ? null : ExecutionIdentityResolver.MatchLive(aliases, statuses);
            var trackingObservedAtUtc = live is null
                ? (DateTimeOffset?)null
                : live.LastReceivedAtUtc >= live.LastEventTimeUtc ? live.LastReceivedAtUtc : live.LastEventTimeUtc;
            var driver = load.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var matchedDriver) ? matchedDriver : null;
            var tacho = vehicle is null ? null : ExecutionIdentityResolver.MatchLiveDriverIdentityForVehicle(aliases, driver, tachoStatuses);
            var visits = geofence?.Visits.Where(visit => visit.LoadId == load.Id).OrderBy(visit => visit.EnteredAtUtc).ToList() ?? [];
            var completedStopIds = geofence is null ? new HashSet<Guid>() : GeofencePlanningMatch.CompletedStopIds(load, visits);
 
            var current = live is null ? ((decimal Longitude, decimal Latitude)?)null : (live.Longitude, live.Latitude);
            var currentEta = now;
            var cumulativeDrivingMinutes = 0d;
            var breakDelayMinutes = 0;
            var routeContainsEstimate = false;
            var initialContinuousDriving = tacho is null ? 0 : tacho.BreakMinutes >= 45 ? tacho.DriveMinutes % 270 : Math.Min(tacho.DriveMinutes, 270);
 
            foreach (var stop in load.Stops.OrderBy(stop => stop.Sequence).Where(stop => !completedStopIds.Contains(stop.Id)))
            {
                orders.TryGetValue(stop.OrderId ?? Guid.Empty, out var order);
                var eta = stop.PlannedArrivalUtc;
                var source = eta is null ? "Unavailable" : "Planned";
 
                // A geofence confirms arrival/departure and advances the route, but is not
                // an ETA prerequisite. Fresh DOT position plus stop coordinates is enough
                // for Azure Maps to calculate the current leg while a missing link is repaired.
                if (LiveEtaEligibility.CanRoute(current, stop, trackingObservedAtUtc, now))
                {
                    try
                    {
                        var routeEstimate = await maps.TravelTimeEstimate(current.Value, (stop.Longitude.Value, stop.Latitude.Value), ct);
                        routeContainsEstimate |= routeEstimate.IsApproximate;
                        var travelTime = routeEstimate.TravelTime;
                        cumulativeDrivingMinutes += travelTime.TotalMinutes;
                        var requiredBreaks = tacho is null ? 0 : Math.Max(0, (int)Math.Floor((initialContinuousDriving + cumulativeDrivingMinutes - 0.01) / 270d));
                        if (requiredBreaks * 45 > breakDelayMinutes)
                        {
                            var extraBreakMinutes = requiredBreaks * 45 - breakDelayMinutes;
                            currentEta += TimeSpan.FromMinutes(extraBreakMinutes);
                            breakDelayMinutes += extraBreakMinutes;
                        }
                        currentEta += travelTime;
                        eta = currentEta;
                        source = routeContainsEstimate ? "Estimated" : "Live";
                        current = (stop.Longitude.Value, stop.Latitude.Value);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
                    {
                        source = eta is null ? "Unavailable" : "Planned";
                    }
                }
                var timingRule = SiteTimingRuleMatcher.MatchForLoad(load, stop, timingRules, timingSites);
                var masterWindow = timingRule is null ? new SiteTimingWindow(null, null) : SiteTimingRuleMatcher.DeliveryWindow(timingRule, order?.DeliveryDate ?? planningDate);
                var windowStart = order?.DeliveryWindowStartUtc ?? masterWindow.Start;
                var windowEnd = order?.DeliveryWindowEndUtc ?? masterWindow.End;
                var tachoAssessment = source == "Live"
                    ? TachoAssessment(tacho, cumulativeDrivingMinutes, breakDelayMinutes)
                    : source == "Estimated"
                        ? (Status: "EstimateOnly", Explanation: "Azure Maps live truck routing was unavailable for at least one remaining leg. The resilient road estimate is advisory and cannot declare customer ETA risk.")
                        : (Status: "RouteUnavailable", Explanation: geofence is null
                            ? "Geofence execution was unavailable, so the remaining route could not be proved and no live ETA was issued."
                            : trackingObservedAtUtc is not null && now - trackingObservedAtUtc.Value > RunExecutionEvidenceRules.MaximumLiveTrackingAge
                                ? "Tracking has not been received for more than five minutes, so the planned ETA is retained until a fresh RoadTech/DOT observation arrives."
                                : tacho is null
                                    ? "Live route and current TachoMaster duty are unavailable; this ETA must be verified before export."
                                    : "TachoMaster matched the vehicle, but no fresh live route could be calculated; the planned ETA has not been adjusted for a break.");
                var risk = source == "Live" ? Risk(eta, windowStart, windowEnd) : "Pending";
                records.Add(new DeliveryEtaResponse(load.Id, RunDisplayLabel.For(load), load.Status.ToString(), stop.Id, stop.Sequence, stop.Name,
                    order?.Reference, order?.CustomerCode, vehicle?.Registration, eta, source, windowStart, windowEnd,
                    risk, trackingObservedAtUtc,
                    tacho?.DriverName, tacho?.DriveAvailableTodayMinutes, (int)Math.Ceiling(cumulativeDrivingMinutes), breakDelayMinutes,
                    tachoAssessment.Status, tachoAssessment.Explanation));
            }
        }
        return Ok(new { planningDate, calculatedAtUtc = now, records });
    }
 
    [HttpGet("forecast")]
    public async Task<IActionResult> Forecast([FromQuery] DateOnly? from, CancellationToken ct)
    {
        var firstDate = from ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var lastDate = firstDate.AddDays(6);
        List<Load> loads;
        try
        {
            loads = await db.Loads.AsNoTracking().Include(load => load.Stops)
                .Where(load => load.PlanningDate >= firstDate && load.PlanningDate <= lastDate && load.Status != LoadStatus.Cancelled)
                .OrderBy(load => load.PlanningDate).ThenBy(load => load.Reference).Take(2000).ToListAsync(ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            loads = (await PlanningRegisterStore.ReadLoadsAsync(db, null, ct)).Where(load => load.PlanningDate >= firstDate && load.PlanningDate <= lastDate && load.Status != LoadStatus.Cancelled).ToList();
        }
        await RunOperationalStore.EnrichAsync(db, loads, ct);
        var orderIds = loads.SelectMany(load => load.Stops).Where(stop => stop.OrderId != null).Select(stop => stop.OrderId!.Value).Distinct().ToList();
        var orders = await SafeDictionary(db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)), order => order.Id, ct);
        if (orders.Count == 0 && orderIds.Count > 0) orders = (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct)).Where(order => orderIds.Contains(order.Id)).ToDictionary(order => order.Id);
        var trailers = await SafeDictionary(db.Trailers.AsNoTracking().Where(trailer => trailer.Active), trailer => trailer.Id, ct);
        var activeDrivers = await db.Drivers.AsNoTracking().CountAsync(driver => driver.Active, ct);
        var activeVehicles = await db.Vehicles.AsNoTracking().CountAsync(vehicle => vehicle.Active, ct);
        var activeTrailerCapacity = trailers.Values.Sum(trailer => trailer.StandardCapacity ?? 0);
        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var date = firstDate.AddDays(offset);
            var dayLoads = loads.Where(load => load.PlanningDate == date).ToList();
            var dayOrderIds = dayLoads.SelectMany(load => load.Stops).Where(stop => stop.OrderId != null).Select(stop => stop.OrderId!.Value).Distinct();
            var pallets = (int)Math.Ceiling(dayLoads.Sum(load => load.PalletSpacesUsed ?? 0));
            if (pallets == 0) pallets = dayOrderIds.Sum(id => orders.TryGetValue(id, out var order) ? order.Pallets ?? 0 : 0);
            var plannedCapacity = (int)Math.Ceiling(dayLoads.Sum(load => load.TotalPalletSpaces ?? 0));
            var utilisation = plannedCapacity > 0 ? Math.Round((decimal)pallets / plannedCapacity * 100, 1) : (decimal?)null;
            var overCapacityLoads = dayLoads.Count(load => load.TotalPalletSpaces > 0 && load.PalletSpacesUsed > load.TotalPalletSpaces);
            var emptyMiles = dayLoads.Sum(load => load.EmptyMiles ?? 0);
            var assignedDrivers = dayLoads.Where(load => load.DriverId != null).Select(load => load.DriverId).Distinct().Count();
            var assignedVehicles = dayLoads.Where(load => load.VehicleId != null).Select(load => load.VehicleId).Distinct().Count();
            var exceptions = dayLoads.Count(load => load.DriverId is null || load.VehicleId is null || load.Stops.Any(stop => stop.Latitude is null || stop.Longitude is null)
                || load.TotalPalletSpaces > 0 && load.PalletSpacesUsed > load.TotalPalletSpaces);
            return new ForecastDay(date, dayLoads.Count, assignedDrivers, activeDrivers, assignedVehicles, activeVehicles, pallets,
                plannedCapacity > 0 ? plannedCapacity : activeTrailerCapacity, emptyMiles, exceptions, utilisation, overCapacityLoads);
        }).ToList();
        return Ok(new
        {
            from = firstDate,
            to = lastDate,
            generatedAtUtc = DateTimeOffset.UtcNow,
            activeDrivers,
            activeVehicles,
            days,
            totals = new
            {
                loads = days.Sum(day => day.Loads),
                emptyMiles = days.Sum(day => day.EmptyMiles),
                exceptions = days.Sum(day => day.Exceptions),
                plannedPallets = days.Sum(day => day.PlannedPallets),
                availableTrailerPallets = days.Sum(day => day.AvailableTrailerPallets),
                utilisationPercent = days.Sum(day => day.AvailableTrailerPallets) > 0
                    ? Math.Round((decimal)days.Sum(day => day.PlannedPallets) / days.Sum(day => day.AvailableTrailerPallets) * 100, 1) : (decimal?)null,
                overCapacityLoads = days.Sum(day => day.OverCapacityLoads)
            }
        });
    }
 
    private static async Task<Dictionary<TKey, T>> SafeDictionary<T, TKey>(IQueryable<T> query, Func<T, TKey> keySelector, CancellationToken ct) where TKey : notnull
    {
        try { return await query.ToDictionaryAsync(keySelector, ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { return []; }
    }
 
    private static async Task<List<T>> SafeList<T>(IQueryable<T> query, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { return []; }
    }
 
    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) || message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }
 
    internal static (string Status, string Explanation) TachoAssessment(TachoVehicleDriverStatus? tacho, double routeDrivingMinutes, int breakMinutes)
    {
        if (tacho is null) return ("Unavailable", "No live driver card, Falcon driver identity or current TachoMaster duty was matched; verify the driver before promising this ETA.");
        if (tacho.EvidenceSource == "FalconLiveCard" && tacho.DriveAvailableTodayMinutes is null)
            return ("CardConfirmedHoursUnavailable", $"Falcon confirms {tacho.DriverName} is in the vehicle, but TachoMaster did not return remaining-drive metrics. ETA cannot safely include legal break calculations.");
        if (tacho.DriveAvailableTodayMinutes is null)
            return ("DutyMatchedHoursUnavailable", $"TachoMaster matched {tacho.DriverName}'s duty, but remaining-drive metrics are temporarily unavailable. The duty remains visible, but the ETA is not customer-promise ready until legal-hours availability is confirmed.");
        if (tacho.DriveAvailableTodayMinutes is int remaining && routeDrivingMinutes > remaining)
            return ("InsufficientDriveTime", $"The route needs about {Math.Ceiling(routeDrivingMinutes)} driving minutes but TachoMaster shows {remaining} minutes available today. Re-plan or confirm legal availability.");
        if (breakMinutes > 0) return ("BreakIncluded", $"ETA includes {breakMinutes} minutes for a statutory driving break based on current duty/card evidence and route time.");
        return tacho.EvidenceSource == "FalconLiveCard"
            ? ("CardConfirmedWithinDriveTime", "Falcon confirms the card/driver is present and TachoMaster profile availability covers the calculated route; no additional driving break was added.")
            : ("WithinDriveTime", "Current TachoMaster availability covers the calculated route without an additional driving break.");
    }
 
 
    private static string Risk(DateTimeOffset? eta, DateTimeOffset? start, DateTimeOffset? end)
    {
        if (eta is null || end is null) return "Pending";
        if (eta > end) return "Late";
        if (end - eta <= TimeSpan.FromMinutes(30)) return "AtRisk";
        return "OnTrack";
    }
 
    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(value.UtcDateTime); }
    }
}
 
public sealed record DeliveryEtaResponse(Guid LoadId, string LoadReference, string LoadStatus, Guid StopId, int Sequence, string StopName, string? OrderReference, string? CustomerCode, string? VehicleRegistration, DateTimeOffset? EtaUtc, string Source, DateTimeOffset? DeliveryWindowStartUtc, DateTimeOffset? DeliveryWindowEndUtc, string Risk, DateTimeOffset? TrackingUpdatedAtUtc, string? TachoDriverName, int? DriveAvailableTodayMinutes, int RouteDrivingMinutes, int BreakMinutesIncluded, string TachoStatus, string TachoExplanation);
public sealed record ForecastDay(DateOnly Date, int Loads, int AssignedDrivers, int AvailableDrivers, int AssignedVehicles, int AvailableVehicles, int PlannedPallets, int AvailableTrailerPallets, decimal EmptyMiles, int Exceptions, decimal? UtilisationPercent, int OverCapacityLoads);
