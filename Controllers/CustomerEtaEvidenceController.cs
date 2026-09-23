using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Produces one auditable execution chain for customer ETA communication:
/// plan allocation -> Falcon live card/TachoMaster duty -> DOT/Falcon movement/tracking ->
/// geofence execution -> Azure Maps ETA -> legal driving-time assessment.
/// </summary>
[ApiController, Route("api/v1/operations/customer-eta-evidence")]
[Authorize]
public sealed class CustomerEtaEvidenceController(
    TmsDbContext db,
    AzureMapsRouteClient maps,
    TachoMasterClient tachoMaster,
    SiteTimingRuleStore timingRuleStore,
    ILogger<CustomerEtaEvidenceController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        return Ok(await BuildAsync(planningDate, ct));
    }

    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportCsv([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var snapshot = await BuildAsync(planningDate, ct);
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', new[]
        {
            "Generated UTC", "Planning date", "Run", "Order reference", "Customer", "Planned driver", "Live driver",
            "Driver/live match", "Vehicle", "Card/duty observed UTC", "First DOT/Falcon movement after card/duty UTC",
            "Card/duty to movement minutes", "Latest tracking UTC", "Geofence execution available", "Last confirmed site",
            "Last site arrival UTC", "Last site departure UTC", "Delivery stop", "ETA UTC", "ETA source", "Window end UTC", "Risk",
            "Drive available today minutes", "Remaining route driving minutes", "Break included minutes", "Tacho status",
            "Evidence status", "Customer promise ready", "Evidence explanation"
        }.Select(Csv)));

        foreach (var record in snapshot.Records.Where(record => record.IsDelivery))
        {
            csv.AppendLine(string.Join(',', new[]
            {
                snapshot.GeneratedAtUtc.ToString("O"), snapshot.PlanningDate.ToString("yyyy-MM-dd"), record.LoadReference,
                record.OrderReference, record.CustomerCode, record.PlannedDriverName, record.TachoDriverName,
                record.DriverEvidenceStatus, record.VehicleRegistration, Iso(record.TachoSignOnUtc), Iso(record.FirstMovementUtc),
                record.SignOnToMovementMinutes?.ToString(), Iso(record.LatestTrackingUtc), record.GeofenceEvidenceAvailable ? "Yes" : "No",
                record.LastConfirmedSite, Iso(record.LastSiteArrivalUtc), Iso(record.LastSiteDepartureUtc), record.StopName,
                Iso(record.EtaUtc), record.EtaSource, Iso(record.DeliveryWindowEndUtc), record.Risk,
                record.DriveAvailableTodayMinutes?.ToString(), record.RouteDrivingMinutes.ToString(),
                record.BreakMinutesIncluded.ToString(), record.TachoStatus, record.EvidenceStatus,
                record.CustomerPromiseReady ? "Yes" : "No", record.EvidenceExplanation
            }.Select(Csv)));
        }

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"SLH-customer-ETA-evidence-{planningDate:yyyy-MM-dd}.csv");
    }

    private async Task<CustomerEtaEvidenceSnapshot> BuildAsync(DateOnly planningDate, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var loads = (await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .Take(500)
            .ToList();
        var timingSites = await db.Sites.AsNoTracking().Where(site => site.Active).Take(5000).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, timingSites, ct);
        var timingRules = await timingRuleStore.ReadAsync(ct);

        var orderIds = loads.SelectMany(load => load.Stops)
            .Where(stop => stop.OrderId is not null)
            .Select(stop => stop.OrderId!.Value)
            .Distinct().ToList();
        var orders = await SafeDictionary(db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)), order => order.Id, ct);
        if (orders.Count == 0 && orderIds.Count > 0)
            orders = (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct))
                .Where(order => orderIds.Contains(order.Id)).ToDictionary(order => order.Id);

        var vehicleIds = loads.Where(load => load.VehicleId is not null).Select(load => load.VehicleId!.Value).Distinct().ToList();
        var driverIds = loads.Where(load => load.DriverId is not null).Select(load => load.DriverId!.Value).Distinct().ToList();
        var vehicles = await SafeDictionary(db.Vehicles.AsNoTracking().Where(vehicle => vehicleIds.Contains(vehicle.Id)), vehicle => vehicle.Id, ct);
        var drivers = await SafeDictionary(db.Drivers.AsNoTracking().Where(driver => driverIds.Contains(driver.Id)), driver => driver.Id, ct);
        var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(db, vehicles.Values.ToList(), ct);
        var liveStatuses = await SafeList(db.VehicleLiveStatuses.AsNoTracking(), ct);

        // Pull the combined live identity view. Falcon can confirm a newly inserted card before
        // TachoMaster exposes the open duty; TachoMaster duty remains the legal-hours authority
        // when both sources agree. If they disagree, legal-hours authority is suppressed.
        IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> tachoStatuses = new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>();
        try { tachoStatuses = await tachoMaster.GetLiveDriverStatusesByVehicleAsync(planningDate, ct); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster/Falcon live driver evidence was unavailable while building customer ETA evidence.");
        }

        var (startUtc, endUtc) = OperatingWindow(planningDate);
        var trackingEvents = await SafeList(db.VehicleTrackingEvents.AsNoTracking()
            .Where(item => item.EventTimeUtc >= startUtc.AddHours(-2) && item.EventTimeUtc < endUtc.AddHours(12))
            .OrderBy(item => item.EventTimeUtc)
            .Take(30000), ct);

        EmbeddedGeofenceSnapshot? geofence = null;
        try
        {
            geofence = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, GeofencePlanningMatch.PrepareLoads(loads), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Geofence execution evidence was unavailable while building customer ETA evidence.");
            db.ChangeTracker.Clear();
        }
        var geofenceEvidenceAvailable = geofence is not null;

        var records = new List<CustomerEtaEvidenceRecord>();
        foreach (var load in loads)
        {
            var vehicle = load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var matchedVehicle) ? matchedVehicle : null;
            var driver = load.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var matchedDriver) ? matchedDriver : null;
            var aliases = vehicle is not null && aliasesByVehicle.TryGetValue(vehicle.Id, out var knownAliases)
                ? knownAliases
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var live = vehicle is null ? null : ExecutionIdentityResolver.MatchLive(aliases, liveStatuses);
            var driverEvidence = vehicle is null
                ? new LiveDriverEvidence(null, null, null, null, "Unavailable")
                : LiveDriverEvidenceRules.Resolve(aliases, driver, tachoStatuses);
            var displayIdentity = driverEvidence.DisplayIdentity;
            var etaAuthority = driverEvidence.EtaAuthority;
            var observedAt = displayIdentity?.DutyStartUtc;
            var firstMovement = vehicle is null ? null : ExecutionIdentityResolver.FirstMovement(aliases, trackingEvents, observedAt);
            var signOnToMovementMinutes = observedAt is not null && firstMovement is not null
                ? Math.Max(0, (int)Math.Floor((firstMovement.Value - observedAt.Value).TotalMinutes))
                : (int?)null;
            var latestTracking = live is null
                ? (DateTimeOffset?)null
                : live.LastReceivedAtUtc >= live.LastEventTimeUtc ? live.LastReceivedAtUtc : live.LastEventTimeUtc;
            var driverEvidenceStatus = driverEvidence.CardDutyStatus == "Mismatch"
                ? "LiveCardTachoMismatch"
                : ExecutionIdentityResolver.DriverEvidenceStatus(driver, displayIdentity);
            var evidenceStatus = driverEvidence.CardDutyStatus == "Mismatch"
                ? "IdentityMismatch"
                : RunExecutionEvidenceRules.EvidenceStatus(displayIdentity, latestTracking, now);
            var evidenceExplanation = driverEvidence.CardDutyStatus == "Mismatch"
                ? $"Falcon live card reports {driverEvidence.LiveCard?.DriverName ?? "a driver"}, while the open TachoMaster duty reports {driverEvidence.TachoDuty?.DriverName ?? "another driver"}. Legal-hours figures are suppressed until the identities agree."
                : RunExecutionEvidenceRules.Explanation(displayIdentity, firstMovement, latestTracking, now);
            evidenceExplanation += $" Planned/live driver correlation: {driverEvidenceStatus}. Card/Tacho correlation: {driverEvidence.CardDutyStatus}. Geofence execution: {(geofenceEvidenceAvailable ? "available" : "unavailable")}.";

            var visits = geofence?.Visits.Where(visit => visit.LoadId == load.Id && visit.ConfirmedAtUtc is not null)
                .OrderBy(visit => visit.EnteredAtUtc).ToList() ?? [];
            var completedStopIds = geofence is null ? new HashSet<Guid>() : GeofencePlanningMatch.CompletedStopIds(load, visits);
            var lastVisit = visits.LastOrDefault();

            var current = live is null ? ((decimal Longitude, decimal Latitude)?)null : (live.Longitude, live.Latitude);
            var currentEta = now;
            var cumulativeDrivingMinutes = 0d;
            var breakDelayMinutes = 0;
            var routeContainsEstimate = false;
            var initialContinuousDriving = etaAuthority is null ? 0 : etaAuthority.BreakMinutes >= 45 ? etaAuthority.DriveMinutes % 270 : Math.Min(etaAuthority.DriveMinutes, 270);

            // A customer ETA is for the uncompleted journey only. Stops that have a
            // confirmed arrival and departure are evidence, not future route legs.
            foreach (var stop in load.Stops.OrderBy(stop => stop.Sequence).Where(stop => !completedStopIds.Contains(stop.Id)))
            {
                orders.TryGetValue(stop.OrderId ?? Guid.Empty, out var order);
                var eta = stop.PlannedArrivalUtc;
                var etaSource = eta is null ? "Unavailable" : "Planned";

                // Customer promises require a proved remaining sequence and a tracker
                // observation received no older than five minutes. A stationary vehicle can
                // legitimately keep the same provider event timestamp while Falcon continues
                // to confirm the same current position on each poll.
                if (geofenceEvidenceAvailable && current is not null && stop.Longitude is not null && stop.Latitude is not null && latestTracking is not null && now - latestTracking.Value <= RunExecutionEvidenceRules.MaximumLiveTrackingAge)
                {
                    try
                    {
                        var routeEstimate = await maps.TravelTimeEstimate(current.Value, (stop.Longitude.Value, stop.Latitude.Value), ct);
                        routeContainsEstimate |= routeEstimate.IsApproximate;
                        var travelTime = routeEstimate.TravelTime;
                        cumulativeDrivingMinutes += travelTime.TotalMinutes;
                        var requiredBreaks = etaAuthority is null ? 0 : Math.Max(0, (int)Math.Floor((initialContinuousDriving + cumulativeDrivingMinutes - 0.01) / 270d));
                        if (requiredBreaks * 45 > breakDelayMinutes)
                        {
                            var extraBreakMinutes = requiredBreaks * 45 - breakDelayMinutes;
                            currentEta += TimeSpan.FromMinutes(extraBreakMinutes);
                            breakDelayMinutes += extraBreakMinutes;
                        }
                        currentEta += travelTime;
                        eta = currentEta;
                        etaSource = routeContainsEstimate ? "Estimated" : "Live";
                        current = (stop.Longitude.Value, stop.Latitude.Value);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
                    {
                        etaSource = eta is null ? "Unavailable" : "Planned";
                    }
                }

                var timingRule = SiteTimingRuleMatcher.MatchForLoad(load, stop, timingRules, timingSites);
                var masterWindow = timingRule is null ? new SiteTimingWindow(null, null) : SiteTimingRuleMatcher.DeliveryWindow(timingRule, order?.DeliveryDate ?? planningDate);
                var windowStart = order?.DeliveryWindowStartUtc ?? masterWindow.Start;
                var windowEnd = order?.DeliveryWindowEndUtc ?? masterWindow.End;
                var tachoAssessment = driverEvidence.CardDutyStatus == "Mismatch"
                    ? (Status: "IdentityMismatch", Explanation: "Falcon card identity and TachoMaster duty identity disagree, so legal-hours and break calculations are not trusted for this ETA.")
                    : etaSource == "Live"
                        ? OperationsController.TachoAssessment(etaAuthority, cumulativeDrivingMinutes, breakDelayMinutes)
                        : etaSource == "Estimated"
                            ? (Status: "EstimateOnly", Explanation: "Azure Maps live truck routing was unavailable for at least one remaining leg. The resilient road estimate is advisory and is not customer-promise ready.")
                            : (Status: "RouteUnavailable", Explanation: !geofenceEvidenceAvailable
                                ? "Geofence execution was unavailable, so the remaining route could not be proved and no live customer ETA was issued."
                                : latestTracking is not null && now - latestTracking.Value > RunExecutionEvidenceRules.MaximumLiveTrackingAge
                                    ? "Tracking has not been received for more than five minutes, so no live customer ETA is issued until a fresh RoadTech/DOT observation arrives."
                                    : etaAuthority is null
                                        ? "A live route is available but no trusted live-card/TachoMaster legal-hours evidence is available; this ETA must be verified before export."
                                        : "Live driver evidence matched the vehicle, but no fresh live route could be calculated; the planned ETA has not been adjusted for a break.");
                var customerPromiseReady = geofenceEvidenceAvailable && etaSource == "Live" && evidenceStatus == "VerifiedLive" &&
                    driverEvidenceStatus == "Matched" && driverEvidence.CardDutyStatus != "Mismatch" &&
                    tachoAssessment.Status is "WithinDriveTime" or "BreakIncluded" or "CardConfirmedWithinDriveTime" && eta is not null;
                var risk = etaSource == "Live" ? Risk(eta, windowStart, windowEnd) : "Pending";

                records.Add(new CustomerEtaEvidenceRecord(
                    load.Id, RunDisplayLabel.For(load), load.Status.ToString(), stop.Id, stop.Sequence, stop.Name, IsDeliveryStop(stop),
                    order?.Reference, order?.CustomerCode, driver?.DisplayName, displayIdentity?.DriverName, driverEvidenceStatus, vehicle?.Registration,
                    observedAt, firstMovement, signOnToMovementMinutes, latestTracking, geofenceEvidenceAvailable,
                    lastVisit?.Fence.Name, lastVisit?.EnteredAtUtc, lastVisit?.ExitedAtUtc,
                    eta, etaSource, windowStart, windowEnd, risk,
                    etaAuthority?.DriveAvailableTodayMinutes, (int)Math.Ceiling(cumulativeDrivingMinutes), breakDelayMinutes,
                    tachoAssessment.Status, tachoAssessment.Explanation, evidenceStatus, evidenceExplanation, customerPromiseReady));
            }
        }

        return new CustomerEtaEvidenceSnapshot(
            planningDate,
            now,
            "PlanningRegister+TachoMaster+FalconLiveCard+DOT/Falcon+EmbeddedGeofences+AzureMapsTruckLiveTraffic",
            records.Count,
            records.Count(record => record.IsDelivery),
            records.Count(record => record.IsDelivery && record.CustomerPromiseReady),
            records);
    }

    private async Task<Dictionary<TKey, T>> SafeDictionary<T, TKey>(IQueryable<T> query, Func<T, TKey> keySelector, CancellationToken ct) where TKey : notnull
    {
        try { return await query.ToDictionaryAsync(keySelector, ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { db.ChangeTracker.Clear(); return []; }
    }

    private async Task<List<T>> SafeList<T>(IQueryable<T> query, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { db.ChangeTracker.Clear(); return []; }
    }

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeliveryStop(LoadStop stop) => stop.Name.StartsWith("Deliver", StringComparison.OrdinalIgnoreCase);

    private static string Risk(DateTimeOffset? eta, DateTimeOffset? start, DateTimeOffset? end)
    {
        if (eta is null || end is null) return "Pending";
        if (eta > end) return "Late";
        if (end - eta <= TimeSpan.FromMinutes(30)) return "AtRisk";
        return "OnTrack";
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

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(value.UtcDateTime); }
    }

    private static string Iso(DateTimeOffset? value) => value?.ToString("O") ?? string.Empty;
    private static string Csv(string? value)
    {
        value ??= string.Empty;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

public sealed record CustomerEtaEvidenceSnapshot(
    DateOnly PlanningDate,
    DateTimeOffset GeneratedAtUtc,
    string Source,
    int RecordCount,
    int DeliveryCount,
    int CustomerPromiseReadyCount,
    IReadOnlyList<CustomerEtaEvidenceRecord> Records);

public sealed record CustomerEtaEvidenceRecord(
    Guid LoadId,
    string LoadReference,
    string LoadStatus,
    Guid StopId,
    int Sequence,
    string StopName,
    bool IsDelivery,
    string? OrderReference,
    string? CustomerCode,
    string? PlannedDriverName,
    string? TachoDriverName,
    string DriverEvidenceStatus,
    string? VehicleRegistration,
    DateTimeOffset? TachoSignOnUtc,
    DateTimeOffset? FirstMovementUtc,
    int? SignOnToMovementMinutes,
    DateTimeOffset? LatestTrackingUtc,
    bool GeofenceEvidenceAvailable,
    string? LastConfirmedSite,
    DateTimeOffset? LastSiteArrivalUtc,
    DateTimeOffset? LastSiteDepartureUtc,
    DateTimeOffset? EtaUtc,
    string EtaSource,
    DateTimeOffset? DeliveryWindowStartUtc,
    DateTimeOffset? DeliveryWindowEndUtc,
    string Risk,
    int? DriveAvailableTodayMinutes,
    int RouteDrivingMinutes,
    int BreakMinutesIncluded,
    string TachoStatus,
    string TachoExplanation,
    string EvidenceStatus,
    string EvidenceExplanation,
    bool CustomerPromiseReady);
