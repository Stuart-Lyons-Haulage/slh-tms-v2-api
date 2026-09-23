using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class PreDispatchSafetyService
{
    private readonly TmsDbContext db;
    private readonly TimeProvider timeProvider;

    public PreDispatchSafetyService(TmsDbContext db) : this(db, TimeProvider.System) { }

    public PreDispatchSafetyService(TmsDbContext db, TimeProvider timeProvider)
    {
        this.db = db;
        this.timeProvider = timeProvider;
    }

    public async Task<PreDispatchReadinessResult> EvaluateAsync(Guid loadId, CancellationToken ct)
    {
        var load = await Slh.Tms.Api.Controllers.PlanningResilience.ReadLoadAsync(db, loadId, ct)
            ?? throw new PreDispatchException("RunNotFound", "The run could not be found.");
        await RunOperationalStore.EnrichAsync(db, [load], ct);
        var evidenceAt = timeProvider.GetUtcNow();
        var checks = new List<PreDispatchCheck>();

        checks.Add(Check(
            "DispatchableStatus",
            load.Status is LoadStatus.Draft or LoadStatus.Planned,
            "Critical",
            load.Status is LoadStatus.Draft or LoadStatus.Planned
                ? $"Run is {load.Status} and can enter the dispatch gate."
                : $"Run status {load.Status} cannot be dispatched through this gate."));

        checks.Add(Check("DriverAllocated", load.DriverId is not null, "Critical", load.DriverId is not null
            ? "A driver is allocated."
            : "A driver must be allocated before dispatch."));
        checks.Add(Check("VehicleAllocated", load.VehicleId is not null, "Critical", load.VehicleId is not null
            ? "A vehicle is allocated."
            : "A vehicle must be allocated before dispatch."));

        if (load.DriverId is Guid driverId)
        {
            var driver = await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == driverId, ct);
            checks.Add(Check("DriverActive", driver?.Active == true, "Critical", driver?.Active == true
                ? "Allocated driver is active."
                : "Allocated driver is missing or inactive."));

            // Driving-licence validation is intentionally paused. SLH currently uses Driver Hire
            // for licence checks and will only re-enable an automated dispatch gate once an
            // authoritative Driver Hire integration is available.
        }

        if (load.VehicleId is Guid vehicleId)
        {
            var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == vehicleId, ct);
            checks.Add(Check("VehicleActive", vehicle?.Active == true, "Critical", vehicle?.Active == true
                ? "Allocated vehicle is active."
                : "Allocated vehicle is missing or inactive."));
        }

        if (load.TrailerId is Guid trailerId)
        {
            var trailer = await db.Trailers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == trailerId, ct);
            checks.Add(Check("TrailerActive", trailer?.Active == true, "Critical", trailer?.Active == true
                ? "Allocated trailer is active."
                : "Allocated trailer is missing or inactive."));
        }
        else
        {
            checks.Add(Check("TrailerAllocated", false, "Warning", "No trailer is allocated; planner acknowledgement is required before dispatch."));
        }

        var orderedStops = load.Stops.OrderBy(item => item.Sequence).ToList();
        checks.Add(Check("StopsPresent", orderedStops.Count >= 2, "Critical", orderedStops.Count >= 2
            ? $"Run has {orderedStops.Count} planned stops."
            : "At least two planned stops are required before dispatch."));
        checks.Add(Check("StopsNamed", orderedStops.Count > 0 && orderedStops.All(item => !string.IsNullOrWhiteSpace(item.Name)), "Critical",
            orderedStops.Count > 0 && orderedStops.All(item => !string.IsNullOrWhiteSpace(item.Name))
                ? "All planned stops are named."
                : "Every planned stop must have a name."));

        var mappedStops = orderedStops.Where(item => item.Latitude is not null && item.Longitude is not null).ToList();
        checks.Add(Check("StopsMapped", mappedStops.Count == orderedStops.Count && orderedStops.Count > 0, "Warning",
            mappedStops.Count == orderedStops.Count && orderedStops.Count > 0
                ? "All planned stops have map coordinates."
                : "One or more stops are not mapped; route evidence is incomplete."));

        if (load.PalletSpacesUsed is decimal used && load.TotalPalletSpaces is decimal capacity && capacity >= 0)
        {
            var withinCapacity = capacity == 0 ? used == 0 : used <= capacity;
            var severity = !withinCapacity && capacity > 0 ? "Warning" : "Critical";
            checks.Add(Check("CapacityWithinLimit", withinCapacity, severity,
                capacity == 0 ? (used == 0 ? "No load capacity is required." : "Load units are planned but run capacity is zero.")
                : used <= capacity ? $"Planned load {used:0.##}/{capacity:0.##} is within capacity."
                : $"Planned load {used:0.##} exceeds the recorded capacity {capacity:0.##}. Confirm the actual pallet footprint/equipment before dispatch; planner acknowledgement is required."));
        }
        else
        {
            checks.Add(Check("CapacityVerified", false, "Warning", "Run capacity is not fully recorded; planner acknowledgement is required."));
        }

        var estimatedDriveMinutes = EstimateDriveMinutes(orderedStops);
        await AddResourceConflictChecks(load, checks, ct);

        var classification = checks.Any(item => !item.Passed && item.Severity == "Critical")
            ? "Blocked"
            : checks.Any(item => !item.Passed)
                ? "Unverified"
                : "Recommended";
        return new PreDispatchReadinessResult(
            load.Id,
            classification,
            classification == "Recommended",
            classification == "Unverified",
            estimatedDriveMinutes,
            evidenceAt,
            checks);
    }

    private async Task AddResourceConflictChecks(Load load, List<PreDispatchCheck> checks, CancellationToken ct)
    {
        var others = (await Slh.Tms.Api.Controllers.PlanningResilience.ReadLoadsAsync(db, load.PlanningDate, ct))
            .Where(item => item.Id != load.Id && item.Status is LoadStatus.Planned or LoadStatus.Dispatched or LoadStatus.InProgress)
            .ToList();
        var targetSpan = Span(load);
        foreach (var resource in Resources(load))
        {
            var matches = others.Where(item => ResourceMatches(item, resource.Kind, resource.Id)).ToList();
            if (matches.Count == 0)
            {
                checks.Add(Check($"{resource.Kind}Conflict", true, "Information", $"No other active run uses this {resource.Kind.ToLowerInvariant()} on the planning date."));
                continue;
            }

            var overlap = targetSpan is not null && matches.Any(item => Span(item) is { } otherSpan && Overlaps(targetSpan.Value, otherSpan));
            if (overlap)
            {
                checks.Add(Check($"{resource.Kind}Conflict", false, "Critical", $"The allocated {resource.Kind.ToLowerInvariant()} overlaps another active run on the planning date."));
            }
            else if (targetSpan is null || matches.Any(item => Span(item) is null))
            {
                checks.Add(Check($"{resource.Kind}ScheduleUnverified", false, "Warning", $"The allocated {resource.Kind.ToLowerInvariant()} is used on another run that day and timed-stop evidence is incomplete."));
            }
            else
            {
                checks.Add(Check($"{resource.Kind}Conflict", true, "Information", $"Other same-day use of the allocated {resource.Kind.ToLowerInvariant()} does not overlap this run's timed window."));
            }
        }
    }

    private static IEnumerable<(string Kind, Guid Id)> Resources(Load load)
    {
        if (load.DriverId is Guid driverId) yield return ("Driver", driverId);
        if (load.VehicleId is Guid vehicleId) yield return ("Vehicle", vehicleId);
        if (load.TrailerId is Guid trailerId) yield return ("Trailer", trailerId);
    }

    private static bool ResourceMatches(Load load, string kind, Guid id) => kind switch
    {
        "Driver" => load.DriverId == id,
        "Vehicle" => load.VehicleId == id,
        "Trailer" => load.TrailerId == id,
        _ => false
    };

    private static (DateTimeOffset Start, DateTimeOffset End)? Span(Load load)
    {
        var timed = load.Stops.Where(item => item.PlannedArrivalUtc is not null).OrderBy(item => item.PlannedArrivalUtc).ToList();
        if (timed.Count < 2) return null;
        var start = timed.First().PlannedArrivalUtc!.Value;
        var end = timed.Last().PlannedArrivalUtc!.Value;
        return end >= start ? (start, end) : null;
    }

    private static bool Overlaps((DateTimeOffset Start, DateTimeOffset End) left, (DateTimeOffset Start, DateTimeOffset End) right) =>
        left.Start < right.End && right.Start < left.End;

    private static int? EstimateDriveMinutes(IReadOnlyList<LoadStop> stops)
    {
        if (stops.Count < 2 || stops.Any(item => item.Latitude is null || item.Longitude is null)) return null;
        decimal miles = 0m;
        for (var index = 1; index < stops.Count; index++)
            miles += EstimatedRoadMiles((stops[index - 1].Latitude!.Value, stops[index - 1].Longitude!.Value), (stops[index].Latitude!.Value, stops[index].Longitude!.Value));
        return Math.Max(1, (int)Math.Ceiling((double)(miles / 45m * 60m)));
    }

    private static decimal EstimatedRoadMiles((decimal Lat, decimal Lon) a, (decimal Lat, decimal Lon) b)
    {
        const double radiusMiles = 3958.7613;
        var lat1 = DegreesToRadians((double)a.Lat);
        var lat2 = DegreesToRadians((double)b.Lat);
        var dLat = lat2 - lat1;
        var dLon = DegreesToRadians((double)(b.Lon - a.Lon));
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var crow = 2 * radiusMiles * Math.Asin(Math.Min(1, Math.Sqrt(h)));
        return Math.Round((decimal)(crow * 1.18), 1);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
    private static PreDispatchCheck Check(string code, bool passed, string severity, string message) => new(code, passed, severity, message);
}