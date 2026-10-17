using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning"), Authorize]
public sealed class PlannerSuggestionsController(TmsDbContext db, TachoMasterClient tachoMaster, ILogger<PlannerSuggestionsController> logger) : ControllerBase
{
    [HttpGet("day-suggestions")]
    public async Task<IActionResult> DaySuggestions([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var today = UkDate(now);
        var planningDate = date ?? today;
        var previousDate = planningDate.AddDays(-1);

        var orders = await ReadOrders(planningDate, ct);
        var loads = await ReadLoads(planningDate, ct);
        var plannedOrderIds = loads.SelectMany(load => load.Stops ?? [])
            .Where(stop => stop.OrderId is not null)
            .Select(stop => stop.OrderId!.Value)
            .ToHashSet();
        var unplanned = orders
            .Where(order => order.Status != OrderStatus.Cancelled && order.Status != OrderStatus.Delivered && !plannedOrderIds.Contains(order.Id))
            .ToList();

        var previousLoads = (await ReadLoads(previousDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled && load.DriverId is not null)
            .ToList();
        var sameDayLoads = loads
            .Where(load => load.Status != LoadStatus.Cancelled && load.DriverId is not null)
            .ToList();

        var driverIds = sameDayLoads.Concat(previousLoads)
            .Select(load => load.DriverId!.Value)
            .Distinct()
            .ToList();
        var drivers = await SafeList(db.Drivers.AsNoTracking().Where(driver => driverIds.Contains(driver.Id) && driver.Active), ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var driverById = drivers.ToDictionary(driver => driver.Id);

        IReadOnlyList<TachoDriverProfile> tachoProfiles = [];
        try { tachoProfiles = await tachoMaster.GetDriverProfilesAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TachoMaster planning-date availability is unavailable for route suggestions on {PlanningDate}.", planningDate);
        }
        var profileByDriver = drivers.ToDictionary(
            driver => driver.Id,
            driver => MatchTachoProfile(driver, tachoProfiles));

        var sites = await SafeList(db.Sites.AsNoTracking().Where(site => site.Active), ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        var vehicles = await SafeList(db.Vehicles.AsNoTracking().Where(vehicle => vehicle.Active), ct);
        var vehicleById = vehicles.ToDictionary(vehicle => vehicle.Id);
        var liveStatuses = await SafeList(db.VehicleLiveStatuses.AsNoTracking(), ct);

        var positions = BuildDriverPositions(
            sameDayLoads,
            previousLoads,
            driverById,
            vehicleById,
            liveStatuses,
            now,
            planningDate,
            previousDate);

        var candidates = new List<PlannerDaySuggestion>();
        foreach (var position in positions)
        {
            var driver = position.Driver;
            profileByDriver.TryGetValue(driver.Id, out var profile);
            var planningDriveMinutes = PlanningDateDriveMinutes(driver, profile, planningDate, today);
            var tachoSource = PlanningDateTachoSource(driver, profile, planningDate, today);
            if (position.Latitude is null || position.Longitude is null) continue;

            var scored = unplanned.Select(order =>
            {
                var collectionName = CollectionName(order);
                var destinationName = Destination(order);
                var collectionSite = FindSite(sites, collectionName);
                var destinationSite = FindSite(sites, destinationName);
                var distance = collectionSite?.Latitude is not null && collectionSite.Longitude is not null
                    ? HaversineMiles((double)position.Latitude.Value, (double)position.Longitude.Value, (double)collectionSite.Latitude.Value, (double)collectionSite.Longitude.Value)
                    : (double?)null;
                var orderType = OrderType(order);
                var direction = Direction(collectionSite, destinationSite, order);

                var score = distance is null ? -1000d : 100d - Math.Min(distance.Value, 250d);

                // Same-day continuity is significantly more valuable than yesterday-position reuse.
                if (position.IsSameDay) score += 55d;

                // A north-positioned vehicle returning south is the preferred candidate for southbound/backhaul work.
                var southboundContinuation = direction == "Southbound" &&
                    collectionSite?.Latitude is not null &&
                    position.Latitude.Value >= collectionSite.Latitude.Value - 0.35m;
                if (southboundContinuation) score += 75d;

                // Crate/backhaul work receives a smaller bonus when the vehicle is already in the north.
                if (string.Equals(orderType, "Crates", StringComparison.OrdinalIgnoreCase) && position.Latitude >= 52.0m)
                    score += 30d;

                // Avoid presenting clearly impractical repositioning as a strong suggestion.
                if (distance > 120d) score -= 35d;
                if (distance > 180d) score -= 50d;

                // Tacho is evidence, not the allocator. Use the metric for the date being
                // planned (today or TachoMaster's explicit tomorrow value), never today's
                // remaining drive as if it described a future duty.
                if (planningDriveMinutes is int available)
                {
                    var approximateRepositionMinutes = distance is null ? 0 : (int)Math.Ceiling(distance.Value / 45d * 60d);
                    if (available < approximateRepositionMinutes + 60) score -= 80d;
                }

                return new
                {
                    order,
                    collectionName,
                    destinationName,
                    collectionSite,
                    distance,
                    orderType,
                    direction,
                    southboundContinuation,
                    score
                };
            })
            .Where(item => item.distance is not null)
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.distance)
            .Take(3)
            .ToList();

            foreach (var item in scored)
            {
                var continuationText = item.southboundContinuation
                    ? " Same-day southbound continuation: this vehicle/driver is already in the north and naturally returning south."
                    : position.IsSameDay
                        ? " Same-day continuation from the driver's current run/position."
                        : " Previous-day positioning fallback.";
                var crateText = item.orderType == "Crates" ? " Potential crate/backhaul work." : string.Empty;
                var tachoText = planningDriveMinutes is int minutes
                    ? $" Tacho drive available for {planningDate:yyyy-MM-dd}: {minutes / 60}h {minutes % 60:00} ({tachoSource}); confirm any required statutory break before allocation."
                    : $" Tacho drive availability for {planningDate:yyyy-MM-dd} is unconfirmed, so legal availability must still be checked.";
                var vehicleText = string.IsNullOrWhiteSpace(position.VehicleRegistration)
                    ? string.Empty
                    : $" Keep vehicle {position.VehicleRegistration} with the driver where practical.";

                candidates.Add(new PlannerDaySuggestion(
                    driver.Id,
                    driver.DisplayName,
                    driver.EmployeeNumber,
                    position.Load.Id,
                    position.Load.Reference,
                    position.SourceDate,
                    position.LastLocation,
                    position.Latitude,
                    position.Longitude,
                    item.order.Id,
                    item.order.Reference,
                    item.order.CustomerCode,
                    item.collectionName,
                    item.destinationName,
                    item.orderType,
                    item.order.Pallets,
                    item.distance is null ? null : Math.Round(item.distance.Value, 1),
                    driver.TachoDriveAvailableTodayMinutes,
                    planningDriveMinutes,
                    planningDriveMinutes is null ? null : planningDate,
                    tachoSource,
                    item.score,
                    $"{driver.DisplayName} is positioned at {position.LastLocation}. {item.direction} collection at {item.collectionName} is about {item.distance:0.0} miles away.{continuationText}{vehicleText}{crateText}{tachoText}"));
            }
        }

        return Ok(new
        {
            planningDate,
            previousDate,
            generatedAtUtc = now,
            tachoAvailabilityPolicy = planningDate == today
                ? "Current TachoMaster today metric"
                : planningDate == today.AddDays(1)
                    ? "TachoMaster tomorrow metric"
                    : "No future legal-hours estimate beyond tomorrow",
            unplannedOrders = unplanned.Count,
            sameDayDrivers = sameDayLoads.Select(load => load.DriverId).Where(id => id is not null).Distinct().Count(),
            previousDayDrivers = previousLoads.Select(load => load.DriverId).Where(id => id is not null).Distinct().Count(),
            suggestions = candidates
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.RepositionMiles)
                .Take(16)
        });
    }

    private static List<DriverPosition> BuildDriverPositions(
        IReadOnlyList<Load> sameDayLoads,
        IReadOnlyList<Load> previousLoads,
        IReadOnlyDictionary<Guid, Driver> driverById,
        IReadOnlyDictionary<Guid, Vehicle> vehicleById,
        IReadOnlyList<VehicleLiveStatus> liveStatuses,
        DateTimeOffset now,
        DateOnly planningDate,
        DateOnly previousDate)
    {
        var result = new List<DriverPosition>();
        var handledDrivers = new HashSet<Guid>();

        void AddPositions(IEnumerable<IGrouping<Guid, Load>> groups, bool sameDay, DateOnly sourceDate)
        {
            foreach (var driverLoads in groups)
            {
                if (!handledDrivers.Add(driverLoads.Key)) continue;
                if (!driverById.TryGetValue(driverLoads.Key, out var driver)) continue;

                var latestLoad = driverLoads
                    .OrderByDescending(load => LoadOperationalPriority(load.Status))
                    .ThenByDescending(load => LatestPlannedStop(load))
                    .ThenByDescending(load => load.CreatedAtUtc)
                    .First();
                var finalStop = (latestLoad.Stops ?? []).OrderBy(stop => stop.Sequence).LastOrDefault();

                decimal? latitude = finalStop?.Latitude;
                decimal? longitude = finalStop?.Longitude;
                var lastLocation = finalStop?.Name ?? (sameDay ? "Current run final stop" : "Previous final stop");
                string? vehicleRegistration = null;

                if (latestLoad.VehicleId is Guid vehicleId && vehicleById.TryGetValue(vehicleId, out var vehicle))
                {
                    vehicleRegistration = vehicle.Registration;
                    var keys = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(Normalise)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var live = liveStatuses
                        .Where(status => keys.Contains(Normalise(status.VehicleIdentifier)))
                        .OrderByDescending(status => status.LastEventTimeUtc)
                        .FirstOrDefault();

                    var freshness = sameDay && planningDate == UkDate(now) ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(18);
                    if (live is not null && now - live.LastEventTimeUtc <= freshness)
                    {
                        latitude = live.Latitude;
                        longitude = live.Longitude;
                        lastLocation = $"Live position · {vehicle.Registration}";
                    }
                }

                result.Add(new DriverPosition(driver, latestLoad, sourceDate, sameDay, vehicleRegistration, lastLocation, latitude, longitude));
            }
        }

        AddPositions(sameDayLoads.GroupBy(load => load.DriverId!.Value), true, planningDate);
        AddPositions(previousLoads.GroupBy(load => load.DriverId!.Value), false, previousDate);
        return result;
    }

    private async Task<List<TransportOrder>> ReadOrders(DateOnly date, CancellationToken ct)
    {
        try
        {
            var result = await db.TransportOrders.AsNoTracking()
                .Where(order => order.CollectionDate == date && order.Status != OrderStatus.Cancelled)
                .OrderBy(order => order.Reference).Take(1500).ToListAsync(ct);
            if (result.Count > 0) return result;
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            logger.LogWarning(ex, "Planner suggestions are using the fallback order register.");
            db.ChangeTracker.Clear();
        }
        return await PlanningRegisterStore.ReadOrdersAsync(db, date, date, ct);
    }

    private async Task<List<Load>> ReadLoads(DateOnly date, CancellationToken ct)
    {
        try
        {
            var result = await db.Loads.AsNoTracking().Include(load => load.Stops)
                .Where(load => load.PlanningDate == date && load.Status != LoadStatus.Cancelled)
                .OrderBy(load => load.Reference).Take(1000).ToListAsync(ct);
            if (result.Count > 0) return result;
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            logger.LogWarning(ex, "Planner suggestions are using the fallback planning register.");
            db.ChangeTracker.Clear();
        }
        return await PlanningRegisterStore.ReadLoadsAsync(db, date, ct);
    }

    private static async Task<List<T>> SafeList<T>(IQueryable<T> query, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch { return []; }
    }

    private static TachoDriverProfile? MatchTachoProfile(Driver driver, IEnumerable<TachoDriverProfile> profiles)
    {
        if (int.TryParse(driver.TachoMasterDriverId, out var memberCode) && memberCode > 0)
        {
            var byMember = profiles.FirstOrDefault(profile => profile.MemberCode == memberCode);
            if (byMember is not null) return byMember;
        }

        if (!string.IsNullOrWhiteSpace(driver.TachoCardNumber))
        {
            var byCard = profiles.FirstOrDefault(profile => CardsMatch(driver.TachoCardNumber, profile.CardNumber));
            if (byCard is not null) return byCard;
        }

        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber))
        {
            var employee = Normalise(driver.EmployeeNumber);
            var byEmployee = profiles.FirstOrDefault(profile => !string.IsNullOrWhiteSpace(profile.EmployeeNumber) && Normalise(profile.EmployeeNumber) == employee);
            if (byEmployee is not null) return byEmployee;
        }

        var names = new[] { driver.TachoName, driver.DisplayName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ExecutionIdentityResolver.NormalisePerson(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return profiles.FirstOrDefault(profile => names.Contains(ExecutionIdentityResolver.NormalisePerson(profile.DriverName)));
    }

    private static int? PlanningDateDriveMinutes(Driver driver, TachoDriverProfile? profile, DateOnly planningDate, DateOnly today)
    {
        if (planningDate == today)
            return profile?.DriveAvailableTodayMinutes ?? driver.TachoDriveAvailableTodayMinutes;
        if (planningDate == today.AddDays(1))
            return profile?.DriveAvailableTomorrowMinutes;
        return null;
    }

    private static string PlanningDateTachoSource(Driver driver, TachoDriverProfile? profile, DateOnly planningDate, DateOnly today)
    {
        if (planningDate == today && profile?.DriveAvailableTodayMinutes is not null) return "TachoMaster live profile · today";
        if (planningDate == today && driver.TachoDriveAvailableTodayMinutes is not null) return "TachoMaster synced profile · today";
        if (planningDate == today.AddDays(1) && profile?.DriveAvailableTomorrowMinutes is not null) return "TachoMaster live profile · tomorrow";
        return "unconfirmed";
    }

    private static bool CardsMatch(string? left, string? right)
    {
        var a = Normalise(left);
        var b = Normalise(right);
        return a.Length >= 8 && b.Length >= 8 &&
               (a == b || a.EndsWith(b, StringComparison.OrdinalIgnoreCase) || b.EndsWith(a, StringComparison.OrdinalIgnoreCase));
    }

    private static Site? FindSite(IEnumerable<Site> sites, string? value)
    {
        var key = Normalise(value);
        if (key.Length == 0) return null;
        return sites.FirstOrDefault(site =>
            new[] { site.ExternalCode, site.Name, site.DriverTextName }
                .Concat((site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Any(candidate => Normalise(candidate) == key));
    }

    private static string CollectionName(TransportOrder order) =>
        order.SellerName ?? Tag(order.DriverInstructions, "Collection site") ?? "Collection not mapped";

    private static string Destination(TransportOrder order) =>
        order.StallNumber ?? Tag(order.DriverInstructions, "Depot") ?? order.MarketName ?? order.Reference;

    private static string OrderType(TransportOrder order)
    {
        var tagged = Tag(order.DriverInstructions, "Order type");
        if (!string.IsNullOrWhiteSpace(tagged)) return tagged;
        var combined = $"{order.DriverInstructions} {order.StallNumber} {order.MarketName}";
        return combined.Contains("crate", StringComparison.OrdinalIgnoreCase) ? "Crates" : "Pallets";
    }

    private static string Direction(Site? collection, Site? destination, TransportOrder order)
    {
        var text = $"{order.Reference} {order.DriverInstructions} {order.MarketName} {order.StallNumber}";
        if (text.Contains("southbound", StringComparison.OrdinalIgnoreCase)) return "Southbound";
        if (text.Contains("northbound", StringComparison.OrdinalIgnoreCase)) return "Northbound";
        if (collection?.Latitude is decimal cLat && destination?.Latitude is decimal dLat)
        {
            if (dLat <= cLat - 0.25m) return "Southbound";
            if (dLat >= cLat + 0.25m) return "Northbound";
        }
        return "Cross-country/local";
    }

    private static string? Tag(string? notes, string label)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var prefix = $"{label}:";
        return notes.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();
    }

    private static DateTimeOffset LatestPlannedStop(Load load) =>
        (load.Stops ?? []).Where(stop => stop.PlannedArrivalUtc is not null).Select(stop => stop.PlannedArrivalUtc!.Value).DefaultIfEmpty(load.CreatedAtUtc).Max();

    private static int LoadOperationalPriority(LoadStatus status) => status switch
    {
        LoadStatus.InProgress => 5,
        LoadStatus.Dispatched => 4,
        LoadStatus.Completed => 3,
        LoadStatus.Planned => 2,
        LoadStatus.Draft => 1,
        _ => 0
    };

    private static double HaversineMiles(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthMiles = 3958.7613;
        static double Radians(double degrees) => degrees * Math.PI / 180d;
        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthMiles * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static DateOnly UkDate(DateTimeOffset value)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record DriverPosition(
        Driver Driver,
        Load Load,
        DateOnly SourceDate,
        bool IsSameDay,
        string? VehicleRegistration,
        string LastLocation,
        decimal? Latitude,
        decimal? Longitude);
}

public sealed record PlannerDaySuggestion(
    Guid DriverId,
    string DriverName,
    string EmployeeNumber,
    Guid PreviousLoadId,
    string PreviousLoadReference,
    DateOnly PreviousPlanningDate,
    string LastLocation,
    decimal? LastLatitude,
    decimal? LastLongitude,
    Guid OrderId,
    string OrderReference,
    string CustomerCode,
    string CollectionSite,
    string Destination,
    string OrderType,
    int? Quantity,
    double? RepositionMiles,
    int? DriveAvailableTodayMinutes,
    int? DriveAvailablePlanningDateMinutes,
    DateOnly? DriveAvailabilityDate,
    string DriveAvailabilitySource,
    double Score,
    string Reason);
