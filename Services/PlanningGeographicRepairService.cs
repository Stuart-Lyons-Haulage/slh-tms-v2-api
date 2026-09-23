using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Rebuilds generated optimiser runs around the physical geography of the work before the
/// proposal is shown to a planner. The original optimiser is deliberately left intact as the
/// evidence/capacity generator; this layer prevents obviously incompatible collection/delivery
/// corridors from being packed together and prevents one resource being silently selected for
/// every run.
/// </summary>
public sealed class PlanningGeographicRepairService(
    TmsDbContext db,
    ILogger<PlanningGeographicRepairService> logger)
{
    private const int MaxCandidateDrivers = 80;
    private const int MaxCandidateVehicles = 80;
    private const int MaxReviewedCandidates = 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly PlanningConstraintEvaluator constraintEvaluator = new();
    private readonly PlanningCandidateRanker candidateRanker = new();

    public async Task RepairAsync(Guid proposalId, DateOnly planningDate, DateTimeOffset evidenceAt, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var proposal = await db.PlanProposals
            .Include(item => item.Runs).ThenInclude(run => run.Allocations)
            .Include(item => item.Runs).ThenInclude(run => run.Candidates)
            .SingleAsync(item => item.Id == proposalId, ct);

        var existingRuns = proposal.Runs.Where(run => !run.IsLocked).ToList();
        if (existingRuns.Count == 0) return;

        var allocations = existingRuns
            .SelectMany(run => run.Allocations)
            .Select(allocation => new SourceAllocation(
                allocation.SourceLineId,
                allocation.Pallets,
                allocation.PalletType,
                allocation.CollectionSite,
                allocation.DeliverySite))
            .ToList();
        if (allocations.Count == 0) return;

        var sourceLineIds = allocations.Select(item => item.SourceLineId).Distinct().ToList();
        var sourceLines = await db.OrderSourceLines.AsNoTracking()
            .Where(item => sourceLineIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, ct);

        var sites = await db.Sites.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        var routeAllocations = allocations.Select(item =>
        {
            sourceLines.TryGetValue(item.SourceLineId, out var sourceLine);
            var collectionSite = MatchSite(sites, item.CollectionSite);
            var deliverySite = MatchSite(sites, item.DeliverySite);
            var route = new PlanningGeographyRoute(
                Normalise(item.CollectionSite),
                Normalise(item.DeliverySite),
                Point(collectionSite),
                Point(deliverySite));
            return new RouteAllocation(item, sourceLine?.CollectionTimeFrom, route);
        }).ToList();

        var groups = BuildGroups(routeAllocations);
        var trailers = await db.Trailers.AsNoTracking().Where(item => item.Active).OrderBy(item => item.TrailerNumber).ToListAsync(ct);
        var lockedTrailerIds = proposal.Runs.Where(run => run.IsLocked && run.TrailerId is not null).Select(run => run.TrailerId!.Value).ToHashSet();
        var availableTrailers = trailers.Where(item => !lockedTrailerIds.Contains(item.Id)).ToList();
        var usedTrailerIds = new HashSet<Guid>();

        db.PlanProposalCandidates.RemoveRange(existingRuns.SelectMany(run => run.Candidates));
        db.PlanProposalAllocations.RemoveRange(existingRuns.SelectMany(run => run.Allocations));
        db.PlanProposalRuns.RemoveRange(existingRuns);
        proposal.Runs.RemoveAll(run => !run.IsLocked);

        var nextSequence = proposal.Runs.Count == 0 ? 0 : proposal.Runs.Max(run => run.Sequence);
        foreach (var group in groups)
        {
            var remaining = group.Items.ToList();
            while (remaining.Count > 0)
            {
                var palletType = remaining[0].Allocation.PalletType;
                var trailer = availableTrailers
                    .Where(item => !usedTrailerIds.Contains(item.Id))
                    .Where(item => TrailerCapacity(item, palletType) > 0)
                    .OrderBy(item => TrailerCapacity(item, palletType))
                    .ThenBy(item => item.TrailerNumber, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                var capacity = trailer is null ? DefaultCapacity(palletType) : TrailerCapacity(trailer, palletType);
                if (trailer is not null) usedTrailerIds.Add(trailer.Id);

                nextSequence++;
                var run = new PlanProposalRun
                {
                    ProposalId = proposal.Id,
                    Sequence = nextSequence,
                    Reference = $"OPT-{planningDate:yyyyMMdd}-{proposal.Period}-{nextSequence:00}",
                    IsLocked = false,
                    TrailerId = trailer?.Id,
                    Classification = trailer is null ? "Blocked" : proposal.Classification,
                    CapacityPallets = capacity,
                    PlannedPallets = 0,
                    Score = 0m,
                    ScoreComponentsJson = "[]",
                    ExplanationJson = "[]"
                };

                var runRemaining = capacity;
                while (remaining.Count > 0 && runRemaining > 0)
                {
                    var item = remaining[0];
                    var quantity = Math.Min(item.Allocation.Pallets, runRemaining);
                    run.Allocations.Add(new PlanProposalAllocation
                    {
                        ProposalRunId = run.Id,
                        SourceLineId = item.Allocation.SourceLineId,
                        Pallets = quantity,
                        PalletType = item.Allocation.PalletType,
                        CollectionSite = item.Allocation.CollectionSite,
                        DeliverySite = item.Allocation.DeliverySite
                    });
                    run.PlannedPallets += quantity;
                    runRemaining -= quantity;

                    if (quantity == item.Allocation.Pallets)
                        remaining.RemoveAt(0);
                    else
                        remaining[0] = item with { Allocation = item.Allocation with { Pallets = item.Allocation.Pallets - quantity } };
                }

                if (trailer is null && remaining.Count > 0)
                {
                    // No safe trailer is available. Keep the whole remaining corridor visible in a
                    // blocked run rather than silently reusing a trailer already allocated elsewhere.
                    foreach (var item in remaining)
                    {
                        run.Allocations.Add(new PlanProposalAllocation
                        {
                            ProposalRunId = run.Id,
                            SourceLineId = item.Allocation.SourceLineId,
                            Pallets = item.Allocation.Pallets,
                            PalletType = item.Allocation.PalletType,
                            CollectionSite = item.Allocation.CollectionSite,
                            DeliverySite = item.Allocation.DeliverySite
                        });
                        run.PlannedPallets += item.Allocation.Pallets;
                    }
                    remaining.Clear();
                    run.ExplanationJson = JsonSerializer.Serialize(new[]
                    {
                        "Blocked: no unused compatible trailer is available for this geographic corridor; the optimiser will not reuse a trailer on the same planning day.",
                        GeographicExplanation(group)
                    }, JsonOptions);
                }

                if (run.PlannedPallets == 0) break;
                SequenceRunStops(run, sourceLines, sites);
                run.ExplanationJson = JsonSerializer.Serialize(new[]
                {
                    $"{run.PlannedPallets}/{run.CapacityPallets} pallet spaces ({Utilisation(run):0.##}% utilisation).",
                    GeographicExplanation(group),
                    "Collections are kept geographically coherent before deliveries; incompatible delivery corridors are not combined just to fill spare pallet space."
                }, JsonOptions);
                proposal.Runs.Add(run);
            }
        }

        await AssignResourceEvidenceAsync(proposal, planningDate, sites, evidenceAt, ct);
        proposal.Classification = WorstClassification(proposal.Runs.Select(run => run.Classification).Append(proposal.Classification));
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Applied geographic optimiser repair to proposal {ProposalId}: {OriginalRuns} source runs became {NewRuns} proposed runs.",
            proposal.Id, existingRuns.Count, proposal.Runs.Count(run => !run.IsLocked));
    }

    private async Task AssignResourceEvidenceAsync(
        PlanProposal proposal,
        DateOnly planningDate,
        IReadOnlyList<Site> sites,
        DateTimeOffset evidenceAt,
        CancellationToken ct)
    {
        var drivers = await db.Drivers.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        drivers = drivers.Where(DriverPopulationRules.IsDriver).ToList();
        var vehicles = await db.Vehicles.AsNoTracking().Where(item => item.Active).OrderBy(item => item.Registration).ToListAsync(ct);
        var liveStatuses = await db.VehicleLiveStatuses.AsNoTracking().OrderByDescending(item => item.LastEventTimeUtc).ToListAsync(ct);
        var recentLoads = await RecentLoadsAsync(planningDate, ct);
        var candidateDrivers = CandidateDrivers(drivers, recentLoads, evidenceAt);
        var candidateVehicles = CandidateVehicles(vehicles, liveStatuses, recentLoads, evidenceAt);
        var consecutiveByDriver = candidateDrivers.ToDictionary(driver => driver.Id, driver => ConsecutiveDays(recentLoads, driver.Id, planningDate));
        var liveByVehicle = candidateVehicles.ToDictionary(vehicle => vehicle.Id, vehicle => MatchLive(vehicle, liveStatuses));
        var orderedLoads = recentLoads.OrderByDescending(load => load.PlanningDate).ThenByDescending(load => load.CreatedAtUtc).ToList();

        var usedDriverIds = proposal.Runs.Where(run => run.IsLocked && run.DriverId is not null).Select(run => run.DriverId!.Value).ToHashSet();
        var usedVehicleIds = proposal.Runs.Where(run => run.IsLocked && run.VehicleId is not null).Select(run => run.VehicleId!.Value).ToHashSet();

        foreach (var run in proposal.Runs.Where(run => !run.IsLocked).OrderBy(run => run.Sequence))
        {
            var first = run.Allocations.OrderBy(item => item.CollectionSequence).FirstOrDefault();
            var last = run.Allocations.OrderByDescending(item => item.DeliverySequence).FirstOrDefault();
            var collection = MatchSite(sites, first?.CollectionSite);
            var delivery = MatchSite(sites, last?.DeliverySite);
            var requiredDrive = PlanningGeography.EstimateDriveMinutes(Point(collection), Point(delivery));
            var candidates = new List<Candidate>();

            foreach (var driver in candidateDrivers)
            foreach (var vehicle in candidateVehicles)
            {
                ct.ThrowIfCancellationRequested();
                var consecutiveDays = consecutiveByDriver[driver.Id];
                var constraints = constraintEvaluator.EvaluateDriver(new PlanningDriverEvidence(
                    driver.Id,
                    requiredDrive,
                    driver.TachoDriveAvailableTodayMinutes,
                    driver.LastTachoSyncUtc,
                    evidenceAt,
                    consecutiveDays,
                    SixthDayAllowed(driver)));
                var live = liveByVehicle[vehicle.Id];
                var previous = orderedLoads.FirstOrDefault(load => load.DriverId == driver.Id || load.VehicleId == vehicle.Id);
                var previousEnd = previous?.Stops.OrderByDescending(stop => stop.Sequence).FirstOrDefault(stop => stop.Latitude is not null);
                var score = candidateRanker.Score(new PlanningCandidateEvidence(
                    vehicle.Id,
                    driver.Id,
                    collection?.Latitude,
                    delivery?.Latitude,
                    live?.Latitude,
                    live?.LastEventTimeUtc,
                    previousEnd?.Latitude,
                    previous is null ? null : StartOfDayUtc(previous.PlanningDate).AddDays(1),
                    consecutiveDays,
                    evidenceAt,
                    run.CapacityPallets == 0 ? 0m : Math.Round((decimal)run.PlannedPallets / run.CapacityPallets * 100m, 2)));
                candidates.Add(new Candidate(driver, vehicle, constraints, score));
            }

            candidates.Sort(CandidateOrder);
            var selected = candidates.FirstOrDefault(candidate => !usedDriverIds.Contains(candidate.Driver.Id) && !usedVehicleIds.Contains(candidate.Vehicle.Id));
            var selectedIndex = selected is null ? -1 : candidates.IndexOf(selected);

            for (var index = 0; index < Math.Min(candidates.Count, MaxReviewedCandidates); index++)
            {
                var candidate = candidates[index];
                var classification = index == selectedIndex
                    ? candidate.Constraints.Classification
                    : candidate.Constraints.Classification == "Recommended" ? "Alternative" : candidate.Constraints.Classification;
                run.Candidates.Add(new PlanProposalCandidate
                {
                    ProposalRunId = run.Id,
                    DriverId = candidate.Driver.Id,
                    VehicleId = candidate.Vehicle.Id,
                    Selected = index == selectedIndex,
                    Classification = classification,
                    PositionSource = candidate.Score.PositionSource,
                    Score = candidate.Score.Total,
                    ScoreComponentsJson = JsonSerializer.Serialize(candidate.Score.Components, JsonOptions),
                    ConstraintResultsJson = JsonSerializer.Serialize(candidate.Constraints.Results, JsonOptions),
                    ExplanationJson = JsonSerializer.Serialize(candidate.Score.Explanations.Concat(candidate.Constraints.Results.Select(result => result.Explanation)).ToList(), JsonOptions)
                });
            }

            if (selected is null)
            {
                run.Classification = "Blocked";
                run.DriverId = null;
                run.VehicleId = null;
                run.PositionSource = null;
                run.Score = 0m;
                run.ScoreComponentsJson = "[]";
                run.ExplanationJson = JsonSerializer.Serialize(DeserializeExplanations(run.ExplanationJson).Append(
                    "Blocked: no unused driver/vehicle combination is available without reusing a resource on the same planning day."), JsonOptions);
                continue;
            }

            usedDriverIds.Add(selected.Driver.Id);
            usedVehicleIds.Add(selected.Vehicle.Id);
            run.DriverId = selected.Driver.Id;
            run.VehicleId = selected.Vehicle.Id;
            run.PositionSource = selected.Score.PositionSource;
            run.Classification = run.Classification == "Blocked" ? "Blocked" : selected.Constraints.Classification;
            run.Score = selected.Score.Total;
            run.ScoreComponentsJson = JsonSerializer.Serialize(selected.Score.Components, JsonOptions);
            run.ExplanationJson = JsonSerializer.Serialize(
                DeserializeExplanations(run.ExplanationJson)
                    .Concat(selected.Score.Explanations)
                    .Concat(selected.Constraints.Results.Select(result => result.Explanation)),
                JsonOptions);
        }
    }

    private static List<GeographicGroup> BuildGroups(IReadOnlyList<RouteAllocation> allocations)
    {
        var groups = new List<GeographicGroup>();
        foreach (var item in allocations
                     .OrderBy(value => value.CollectionTime ?? TimeOnly.MaxValue)
                     .ThenBy(value => value.Allocation.CollectionSite, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.Allocation.DeliverySite, StringComparer.OrdinalIgnoreCase))
        {
            var compatible = groups
                .Where(group => group.PalletFamily == IsEuro(item.Allocation.PalletType))
                .Where(group => PlanningGeography.Compatible(group.Anchor, item.Route))
                .OrderBy(group => GroupCost(group.Anchor, item.Route))
                .FirstOrDefault();
            if (compatible is null)
            {
                compatible = new GeographicGroup(item.Route, IsEuro(item.Allocation.PalletType));
                groups.Add(compatible);
            }
            compatible.Items.Add(item);
        }
        return groups;
    }

    private static double GroupCost(PlanningGeographyRoute left, PlanningGeographyRoute right)
    {
        if (left.Collection is null || left.Delivery is null || right.Collection is null || right.Delivery is null)
            return 0d;
        var collection = PlanningGeography.HaversineMiles(left.Collection, right.Collection);
        var delivery = PlanningGeography.HaversineMiles(left.Delivery, right.Delivery);
        return collection + delivery;
    }

    private static void SequenceRunStops(
        PlanProposalRun run,
        IReadOnlyDictionary<Guid, OrderSourceLine> sourceLines,
        IReadOnlyList<Site> sites)
    {
        var entries = run.Allocations
            .Select(allocation => new
            {
                Allocation = allocation,
                SourceLine = sourceLines.GetValueOrDefault(allocation.SourceLineId),
                Collection = MatchSite(sites, allocation.CollectionSite),
                Delivery = MatchSite(sites, allocation.DeliverySite)
            })
            .ToList();

        var collections = entries
            .OrderBy(item => item.SourceLine?.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(item => item.Allocation.CollectionSite, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Allocation.DeliverySite, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var index = 0; index < collections.Count; index++)
            collections[index].Allocation.CollectionSequence = index + 1;

        var collectionPoints = collections.Select(item => Point(item.Collection)).Where(point => point is not null).Cast<PlanningGeographyPoint>().ToList();
        var anchor = collectionPoints.Count == 0
            ? null
            : new PlanningGeographyPoint(collectionPoints.Average(point => point.Latitude), collectionPoints.Average(point => point.Longitude));
        var deliveries = entries
            .OrderByDescending(item => DeliveryDistance(anchor, Point(item.Delivery)))
            .ThenBy(item => item.Allocation.DeliverySite, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var index = 0; index < deliveries.Count; index++)
            deliveries[index].Allocation.DeliverySequence = entries.Count + index + 1;
    }

    private static double DeliveryDistance(PlanningGeographyPoint? anchor, PlanningGeographyPoint? delivery)
        => anchor is not null && delivery is not null ? PlanningGeography.HaversineMiles(anchor, delivery) : 0d;

    private static string GeographicExplanation(GeographicGroup group)
    {
        var collections = group.Items.Select(item => item.Allocation.CollectionSite).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var deliveries = group.Items.Select(item => item.Allocation.DeliverySite).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return $"Geographic route: {string.Join(", ", collections)} → {string.Join(", ", deliveries)}. {collections.Count} collection site(s) and {deliveries.Count} delivery site(s) share a compatible geographic corridor.";
    }

    private async Task<List<Load>> RecentLoadsAsync(DateOnly planningDate, CancellationToken ct)
    {
        var from = planningDate.AddDays(-7);
        try
        {
            return await db.Loads.AsNoTracking().Include(load => load.Stops)
                .Where(load => load.PlanningDate >= from && load.PlanningDate < planningDate && load.Status != LoadStatus.Cancelled)
                .OrderBy(load => load.PlanningDate).ThenBy(load => load.Reference).ToListAsync(ct);
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            return (await PlanningRegisterStore.ReadLoadsAsync(db, null, ct))
                .Where(load => load.PlanningDate >= from && load.PlanningDate < planningDate && load.Status != LoadStatus.Cancelled)
                .ToList();
        }
    }

    private static IReadOnlyList<Driver> CandidateDrivers(IReadOnlyList<Driver> drivers, IReadOnlyList<Load> recentLoads, DateTimeOffset evidenceAt) => drivers
        .OrderByDescending(driver => driver.TachoDriveAvailableTodayMinutes is not null && driver.LastTachoSyncUtc is not null && evidenceAt - driver.LastTachoSyncUtc <= TimeSpan.FromHours(6))
        .ThenByDescending(driver => recentLoads.Any(load => load.DriverId == driver.Id))
        .ThenBy(driver => driver.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(driver => driver.Id)
        .Take(MaxCandidateDrivers)
        .ToList();

    private static IReadOnlyList<Vehicle> CandidateVehicles(
        IReadOnlyList<Vehicle> vehicles,
        IReadOnlyList<Slh.Tms.Api.Models.Tracking.VehicleLiveStatus> liveStatuses,
        IReadOnlyList<Load> recentLoads,
        DateTimeOffset evidenceAt) => vehicles
        .OrderByDescending(vehicle => MatchLive(vehicle, liveStatuses) is { LastEventTimeUtc: var observedAt } && evidenceAt - observedAt <= TimeSpan.FromMinutes(30))
        .ThenByDescending(vehicle => recentLoads.Any(load => load.VehicleId == vehicle.Id))
        .ThenBy(vehicle => vehicle.Registration, StringComparer.OrdinalIgnoreCase)
        .ThenBy(vehicle => vehicle.Id)
        .Take(MaxCandidateVehicles)
        .ToList();

    private static int ConsecutiveDays(IEnumerable<Load> loads, Guid driverId, DateOnly planningDate)
    {
        var days = loads.Where(load => load.DriverId == driverId).Select(load => load.PlanningDate).ToHashSet();
        var count = 0;
        for (var day = planningDate.AddDays(-1); days.Contains(day); day = day.AddDays(-1)) count++;
        return count;
    }

    private static bool SixthDayAllowed(Driver driver) => driver.Notes?.Contains("sixth day allowed", StringComparison.OrdinalIgnoreCase) == true;

    private static int CandidateOrder(Candidate left, Candidate right)
    {
        var classification = Rank(left.Constraints.Classification).CompareTo(Rank(right.Constraints.Classification));
        if (classification != 0) return classification;
        var score = right.Score.Total.CompareTo(left.Score.Total);
        if (score != 0) return score;
        var driver = string.Compare(left.Driver.DisplayName, right.Driver.DisplayName, StringComparison.OrdinalIgnoreCase);
        return driver != 0 ? driver : string.Compare(left.Vehicle.Registration, right.Vehicle.Registration, StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(string classification) => classification switch { "Recommended" => 0, "Alternative" => 1, "Unverified" => 2, _ => 3 };

    private static string WorstClassification(IEnumerable<string> values) => values.OrderByDescending(Rank).FirstOrDefault() ?? "Unverified";

    private static double? Latitude(Site? site) => site?.Latitude is decimal value ? (double)value : null;
    private static double? Longitude(Site? site) => site?.Longitude is decimal value ? (double)value : null;
    private static PlanningGeographyPoint? Point(Site? site) => Latitude(site) is double lat && Longitude(site) is double lon ? new PlanningGeographyPoint(lat, lon) : null;

    private static Site? MatchSite(IEnumerable<Site> sites, string? value)
    {
        var key = Normalise(value);
        if (key.Length == 0) return null;
        var exact = sites.FirstOrDefault(site => IdentityValues(site).Select(Normalise).Any(candidate => candidate.Length > 0 && candidate == key));
        if (exact is not null) return exact;
        return sites.FirstOrDefault(site => IdentityValues(site).Select(Normalise).Where(candidate => candidate.Length >= 5)
            .Any(candidate => key.Contains(candidate, StringComparison.OrdinalIgnoreCase) || candidate.Contains(key, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> IdentityValues(Site site)
    {
        yield return site.ExternalCode;
        yield return site.Name;
        if (!string.IsNullOrWhiteSpace(site.DriverTextName)) yield return site.DriverTextName;
        foreach (var alias in SplitAliases(site.Aliases)) yield return alias;
    }

    private static IEnumerable<string> SplitAliases(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(new[] { ';', ',', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int TrailerCapacity(Trailer trailer, string? palletType)
        => palletType?.Contains("euro", StringComparison.OrdinalIgnoreCase) == true
            ? trailer.EuroCapacity ?? trailer.StandardCapacity ?? 33
            : trailer.StandardCapacity ?? trailer.EuroCapacity ?? 26;

    private static int DefaultCapacity(string? palletType) => palletType?.Contains("euro", StringComparison.OrdinalIgnoreCase) == true ? 33 : 26;
    private static bool IsEuro(string? palletType) => palletType?.Contains("euro", StringComparison.OrdinalIgnoreCase) == true;
    private static decimal Utilisation(PlanProposalRun run) => run.CapacityPallets <= 0 ? 0m : Math.Round((decimal)run.PlannedPallets / run.CapacityPallets * 100m, 2);
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static DateTimeOffset StartOfDayUtc(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static Slh.Tms.Api.Models.Tracking.VehicleLiveStatus? MatchLive(Vehicle vehicle, IEnumerable<Slh.Tms.Api.Models.Tracking.VehicleLiveStatus> statuses)
    {
        var keys = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalise)
            .ToHashSet();
        return statuses.Where(status => keys.Contains(Normalise(status.VehicleIdentifier)))
            .OrderByDescending(status => status.LastEventTimeUtc)
            .FirstOrDefault();
    }

    private static List<string> DeserializeExplanations(string value)
    {
        try { return JsonSerializer.Deserialize<List<string>>(value, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SourceAllocation(Guid SourceLineId, int Pallets, string? PalletType, string? CollectionSite, string? DeliverySite);
    private sealed record RouteAllocation(SourceAllocation Allocation, TimeOnly? CollectionTime, PlanningGeographyRoute Route);
    private sealed class GeographicGroup(PlanningGeographyRoute anchor, bool palletFamily)
    {
        public PlanningGeographyRoute Anchor { get; } = anchor;
        public bool PalletFamily { get; } = palletFamily;
        public List<RouteAllocation> Items { get; } = [];
    }
    private sealed record Candidate(Driver Driver, Vehicle Vehicle, PlanningConstraintEvaluation Constraints, PlanningCandidateScore Score);
}
