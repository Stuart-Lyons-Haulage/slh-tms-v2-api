using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class PlanningOptimiserService
{
    private const string AllocationType = "planningpalletallocation";
    private const int MaxCandidateDriversPerRun = 80;
    private const int MaxReviewedCandidatesPerRun = 20;
    private const int MaxCandidateVehiclesPerRun = 80;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly TmsDbContext db;
    private readonly ILogger<PlanningOptimiserService> logger;
    private readonly TimeProvider timeProvider;
    private readonly PlanningConstraintEvaluator constraintEvaluator = new();
    private readonly PlanningCandidateRanker candidateRanker = new();

    public PlanningOptimiserService(TmsDbContext db, ILogger<PlanningOptimiserService> logger)
        : this(db, logger, TimeProvider.System)
    {
    }

    public PlanningOptimiserService(
        TmsDbContext db,
        ILogger<PlanningOptimiserService> logger,
        TimeProvider timeProvider)
    {
        this.db = db;
        this.logger = logger;
        this.timeProvider = timeProvider;
    }

    public async Task<PlanProposalResult> GenerateAsync(
        GeneratePlanProposalRequest request,
        string? actor,
        CancellationToken ct)
    {
        var period = NormalisePeriod(request.Period);
        var evidenceAt = timeProvider.GetUtcNow();
        var movements = await db.OrderMovements.AsNoTracking()
            .Where(item => item.LifecycleStatus == OrderMovementStatus.PlannerReady && item.CurrentRevisionId != null)
            .OrderBy(item => item.CustomerCode).ThenBy(item => item.StableMovementKey)
            .ToListAsync(ct);
        var revisionIds = movements.Select(item => item.CurrentRevisionId!.Value).ToList();
        var lines = revisionIds.Count == 0
            ? []
            : await db.OrderSourceLines.AsNoTracking()
                .Where(line => revisionIds.Contains(line.RevisionId) && line.CollectionDate == request.PlanningDate && line.Pallets > 0)
                .OrderBy(line => line.CollectionTimeFrom).ThenBy(line => line.CollectionSite).ThenBy(line => line.DeliverySite).ThenBy(line => line.SourceRowKey)
                .ToListAsync(ct);
        if (period != "FULL_DAY")
            lines = lines.Where(line => Period(line.CollectionTimeFrom) == period).ToList();

        var allocated = await AllocatedPalletsAsync(request.PlanningDate, ct);
        var balances = lines
            .Select(line => new Balance(line, Math.Max(0, line.Pallets!.Value - allocated.GetValueOrDefault(line.Id))))
            .Where(item => item.Remaining > 0)
            .ToList();

        var warnings = new List<PlanProposalWarning>();
        var unmapped = balances.Where(item => string.IsNullOrWhiteSpace(item.Line.CollectionSite) || string.IsNullOrWhiteSpace(item.Line.DeliverySite)).Count();
        if (unmapped > 0)
            warnings.Add(new PlanProposalWarning("AddressMappingRequired", "Warning", $"{unmapped} order line(s) need a collection or delivery site before truck routing can be confirmed. Suggestion: open the site master and map the missing location."));
        var temperatureLines = balances.Count(item => !string.IsNullOrWhiteSpace(item.Line.TemperatureRequirement));
        if (temperatureLines > 0)
            warnings.Add(new PlanProposalWarning("TemperatureCompatibilityReview", "Warning", $"{temperatureLines} line(s) have temperature requirements. Suggestion: confirm the selected trailer is temperature-compatible before applying."));
        var drivers = await db.Drivers.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        drivers = drivers.Where(DriverPopulationRules.IsDriver).ToList();
        var vehicles = await db.Vehicles.AsNoTracking().Where(item => item.Active).OrderBy(item => item.Registration).ToListAsync(ct);
        var trailers = await db.Trailers.AsNoTracking().Where(item => item.Active).OrderBy(item => item.TrailerNumber).ToListAsync(ct);
        var sites = await db.Sites.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        var liveStatuses = await db.VehicleLiveStatuses.AsNoTracking().OrderByDescending(item => item.LastEventTimeUtc).ToListAsync(ct);
        var recentLoads = await RecentLoadsAsync(request.PlanningDate, ct);
        var tachoVerified = drivers.Any(driver => driver.TachoDriveAvailableTodayMinutes is not null && driver.LastTachoSyncUtc is not null && evidenceAt - driver.LastTachoSyncUtc <= TimeSpan.FromHours(6));
        if (!tachoVerified)
            warnings.Add(new PlanProposalWarning("TachoEvidenceMissing", "Warning", "Driver hours evidence is missing or stale; this proposal requires planner acknowledgement before application."));

        var inputHash = Hash(request.PlanningDate, period, balances, allocated, drivers, trailers);
        var nextVersion = (await db.PlanProposals.AsNoTracking()
            .Where(item => item.PlanningDate == request.PlanningDate && item.Period == period)
            .MaxAsync(item => (int?)item.Version, ct) ?? 0) + 1;
        var classification = warnings.Count > 0 ? "Unverified" : "Recommended";
        var proposal = new PlanProposal
        {
            PlanningDate = request.PlanningDate,
            Period = period,
            Version = nextVersion,
            Status = "Generated",
            Classification = classification,
            InputHash = inputHash,
            EvidenceCapturedAtUtc = evidenceAt,
            CreatedAtUtc = evidenceAt,
            CreatedBy = actor,
            EvidenceJson = JsonSerializer.Serialize(new
            {
                capturedAtUtc = evidenceAt,
                sourceLineCount = balances.Count,
                sourcePallets = balances.Sum(item => item.Remaining),
                activeDriverCount = drivers.Count,
                tachoVerified
            }, JsonOptions),
            WarningsJson = JsonSerializer.Serialize(warnings, JsonOptions)
        };

        var lockedRuns = await PlanLockStore.BaselineAsync(db, request.PlanningDate, ct);
        AddLockedRuns(proposal, lockedRuns);
        BuildRuns(proposal, balances, trailers, classification, request.PlanningDate, period);
        AssignCandidateEvidence(proposal, drivers, vehicles, sites, liveStatuses, recentLoads, evidenceAt, ct);
        proposal.Classification = WorstClassification(proposal.Runs.Select(run => run.Classification).Append(proposal.Classification));
        db.PlanProposals.Add(proposal);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Generated immutable planning proposal {ProposalId} version {Version} with {RunCount} runs and classification {Classification}.",
            proposal.Id, proposal.Version, proposal.Runs.Count, proposal.Classification);
        return await ToResult(proposal, warnings);
    }

    public async Task<PlanProposalResult?> GetAsync(Guid id, CancellationToken ct)
    {
        var proposal = await db.PlanProposals.AsNoTracking()
            .Include(item => item.Runs).ThenInclude(run => run.Allocations)
            .Include(item => item.Runs).ThenInclude(run => run.Candidates)
            .SingleOrDefaultAsync(item => item.Id == id, ct);
        if (proposal is null) return null;
        return await ToResult(proposal, DeserializeWarnings(proposal.WarningsJson));
    }

    private static void BuildRuns(PlanProposal proposal, IReadOnlyList<Balance> balances, IReadOnlyList<Trailer> trailers, string classification, DateOnly date, string period)
    {
        var runSequence = proposal.Runs.Count;
        var usedTrailerIds = proposal.Runs.Where(run => run.TrailerId is not null).Select(run => run.TrailerId!.Value).ToHashSet();
        PlanProposalRun? current = null;
        foreach (var balance in balances)
        {
            var remaining = balance.Remaining;
            while (remaining > 0)
            {
                var samePalletType = current is not null && IsEuro(current.Allocations.FirstOrDefault()?.PalletType) == IsEuro(balance.Line.PalletType);
                if (current is null || !samePalletType || current.PlannedPallets >= current.CapacityPallets)
                {
                    var trailer = SelectTrailer(trailers, balance.Line.PalletType, usedTrailerIds);
                    var capacity = TrailerCapacity(trailer, balance.Line.PalletType);
                    runSequence++;
                    current = new PlanProposalRun
                    {
                        Sequence = runSequence,
                        Reference = $"OPT-{date:yyyyMMdd}-{period}-{runSequence:00}",
                        IsLocked = false,
                        TrailerId = trailer?.Id,
                        Classification = trailer is null && trailers.Count > 0 ? "Blocked" : classification,
                        CapacityPallets = capacity,
                        PlannedPallets = 0,
                        Score = 0m,
                        ScoreComponentsJson = "[]",
                        ExplanationJson = "[]"
                    };
                    proposal.Runs.Add(current);
                    if (trailer is not null) usedTrailerIds.Add(trailer.Id);
                }

                var quantity = Math.Min(remaining, current.CapacityPallets - current.PlannedPallets);
                var allocation = new PlanProposalAllocation
                {
                    SourceLineId = balance.Line.Id,
                    Pallets = quantity,
                    PalletType = balance.Line.PalletType,
                    CollectionSite = balance.Line.CollectionSite,
                    DeliverySite = balance.Line.DeliverySite,
                    CollectionSequence = current.Allocations.Count + 1,
                    DeliverySequence = 0
                };
                current.Allocations.Add(allocation);
                current.PlannedPallets += quantity;
                remaining -= quantity;
            }
        }

        foreach (var run in proposal.Runs)
        {
            if (run.IsLocked) continue;
            var collectionCount = run.Allocations.Count;
            for (var index = 0; index < collectionCount; index++)
                run.Allocations[index].DeliverySequence = collectionCount + index + 1;
            var utilisation = run.CapacityPallets == 0 ? 0m : Math.Round((decimal)run.PlannedPallets / run.CapacityPallets * 100m, 2);
            run.Score = utilisation;
            run.ExplanationJson = JsonSerializer.Serialize(new[]
            {
                $"{run.PlannedPallets}/{run.CapacityPallets} pallet spaces ({utilisation}% utilisation).",
                "All collection sequences precede delivery sequences."
            }, JsonOptions);
        }
    }

    private async Task<Dictionary<Guid, int>> AllocatedPalletsAsync(DateOnly date, CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == AllocationType && item.Status == StagingStatus.Promoted)
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(20000)
            .ToListAsync(ct);
        var latest = new Dictionary<string, (Guid SourceLineId, int Pallets)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("sourceLineId", out var source) || source.ValueKind != JsonValueKind.String || !source.TryGetGuid(out var sourceLineId)) continue;
                if (root.TryGetProperty("date", out var dateElement) && DateOnly.TryParse(dateElement.GetString(), out var allocationDate) && allocationDate != date) continue;
                var loadId = root.TryGetProperty("loadId", out var load) ? load.ToString() : string.Empty;
                var key = $"{sourceLineId:N}:{loadId}";
                if (latest.ContainsKey(key)) continue;
                var pallets = root.TryGetProperty("pallets", out var quantity) && quantity.TryGetInt32(out var value) ? Math.Max(value, 0) : 0;
                latest[key] = (sourceLineId, pallets);
            }
            catch (JsonException) { }
        }
        return latest.Values.GroupBy(item => item.SourceLineId).ToDictionary(group => group.Key, group => group.Sum(item => item.Pallets));
    }

    private void AssignCandidateEvidence(
        PlanProposal proposal,
        IReadOnlyList<Driver> drivers,
        IReadOnlyList<Vehicle> vehicles,
        IReadOnlyList<Site> sites,
        IReadOnlyList<Slh.Tms.Api.Models.Tracking.VehicleLiveStatus> liveStatuses,
        IReadOnlyList<Load> recentLoads,
        DateTimeOffset evidenceAt, CancellationToken ct)
    {
        var recentDriverIds = recentLoads.Where(load => load.DriverId is not null).Select(load => load.DriverId!.Value).ToHashSet();
        var recentVehicleIds = recentLoads.Where(load => load.VehicleId is not null).Select(load => load.VehicleId!.Value).ToHashSet();
        var candidateDrivers = CandidateDrivers(drivers, recentDriverIds, evidenceAt);
        var candidateVehicles = CandidateVehicles(vehicles, liveStatuses, recentVehicleIds, evidenceAt);
        var consecutiveByDriver = candidateDrivers.ToDictionary(driver => driver.Id, driver => ConsecutiveDays(recentLoads, driver.Id, proposal.PlanningDate));
        var liveByVehicle = candidateVehicles.ToDictionary(vehicle => vehicle.Id, vehicle => MatchLive(vehicle, liveStatuses));
        var orderedLoads = recentLoads.OrderByDescending(load => load.PlanningDate).ThenByDescending(load => load.CreatedAtUtc).ToList();
        var previousByPair = candidateDrivers.SelectMany(driver => candidateVehicles.Select(vehicle => (driver.Id, VehicleId: vehicle.Id)))
            .ToDictionary(pair => pair, pair => orderedLoads.FirstOrDefault(load => load.DriverId == pair.Id || load.VehicleId == pair.VehicleId));
        foreach (var run in proposal.Runs)
        {
            if (run.IsLocked) continue;
            var first = run.Allocations.OrderBy(item => item.CollectionSequence).FirstOrDefault();
            var last = run.Allocations.OrderByDescending(item => item.DeliverySequence).FirstOrDefault();
            var collection = MatchSite(sites, first?.CollectionSite);
            var delivery = MatchSite(sites, last?.DeliverySite);
            var requiredDrive = RequiredDriveMinutes(collection?.Latitude, delivery?.Latitude);
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
                var previous = previousByPair[(driver.Id, vehicle.Id)];
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
                var candidate = new Candidate(driver, vehicle, constraints, score);
                candidates.Add(candidate);
            }

            candidates.Sort(CandidateOrder);
            var selected = candidates.FirstOrDefault();
            if (selected is null) continue;
            // Rank the full bounded pool, but persist only the review shortlist.
            // Saving 80 × 80 evidence records per run exhausted the HTTP gateway budget.
            for (var index = 0; index < Math.Min(candidates.Count, MaxReviewedCandidatesPerRun); index++)
            {
                var candidate = candidates[index];
                var candidateClassification = index > 0 && candidate.Constraints.Classification == "Recommended"
                    ? "Alternative"
                    : candidate.Constraints.Classification;
                var explanations = candidate.Score.Explanations
                    .Concat(candidate.Constraints.Results.Select(result => result.Explanation))
                    .ToList();
                run.Candidates.Add(new PlanProposalCandidate
                {
                    DriverId = candidate.Driver.Id,
                    VehicleId = candidate.Vehicle.Id,
                    Selected = index == 0,
                    Classification = candidateClassification,
                    PositionSource = candidate.Score.PositionSource,
                    Score = candidate.Score.Total,
                    ScoreComponentsJson = JsonSerializer.Serialize(candidate.Score.Components, JsonOptions),
                    ConstraintResultsJson = JsonSerializer.Serialize(candidate.Constraints.Results, JsonOptions),
                    ExplanationJson = JsonSerializer.Serialize(explanations, JsonOptions)
                });
            }
            run.DriverId = selected.Driver.Id;
            run.VehicleId = selected.Vehicle.Id;
            run.PositionSource = selected.Score.PositionSource;
            run.Classification = selected.Constraints.Classification;
            run.Score = selected.Score.Total;
            run.ScoreComponentsJson = JsonSerializer.Serialize(selected.Score.Components, JsonOptions);
            run.ExplanationJson = JsonSerializer.Serialize(
                selected.Score.Explanations.Concat(selected.Constraints.Results.Select(result => result.Explanation)).ToList(),
                JsonOptions);
        }
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

    private async Task<PlanProposalResult> ToResult(PlanProposal proposal, IReadOnlyList<PlanProposalWarning> warnings)
    {
        var driverIds = proposal.Runs.Where(x => x.DriverId != null).Select(x => x.DriverId!.Value).Distinct().ToList();
        var vehicleIds = proposal.Runs.Where(x => x.VehicleId != null).Select(x => x.VehicleId!.Value).Distinct().ToList();
        var trailerIds = proposal.Runs.Where(x => x.TrailerId != null).Select(x => x.TrailerId!.Value).Distinct().ToList();
        var drivers = await db.Drivers.AsNoTracking().Where(x => driverIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, CancellationToken.None);
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => vehicleIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Registration, CancellationToken.None);
        var trailers = await db.Trailers.AsNoTracking().Where(x => trailerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.TrailerNumber, CancellationToken.None);
        return new(
            proposal.Id,
            proposal.PlanningDate,
            proposal.Period,
            proposal.Version,
            proposal.Status,
            proposal.Classification,
            proposal.InputHash,
            proposal.EvidenceCapturedAtUtc,
            proposal.CreatedAtUtc,
            proposal.CreatedBy,
            warnings,
            proposal.Runs.OrderBy(run => run.Sequence).Select(run => new PlanProposalRunResult(
                run.Id,
                run.Sequence,
                run.Reference,
                run.IsLocked,
                run.LiveLoadId,
                run.Classification,
                run.DriverId,
                run.VehicleId,
                run.TrailerId,
                run.PositionSource,
                run.CapacityPallets,
                run.PlannedPallets,
                run.Score,
                DeserializeComponents(run.ScoreComponentsJson),
                DeserializeExplanations(run.ExplanationJson),
                run.Allocations.OrderBy(allocation => allocation.CollectionSequence).Select(allocation => new PlanProposalAllocationResult(
                    allocation.Id,
                    allocation.SourceLineId,
                    allocation.Pallets,
                    allocation.PalletType,
                    allocation.CollectionSite,
                    allocation.DeliverySite,
                    allocation.CollectionSequence,
                    allocation.DeliverySequence)).ToList(),
                run.Candidates.OrderByDescending(candidate => candidate.Selected).ThenByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.DriverId).ThenBy(candidate => candidate.VehicleId)
                    .Select(candidate => new PlanProposalCandidateResult(
                        candidate.Id,
                        candidate.DriverId,
                        candidate.VehicleId,
                        candidate.Selected,
                        candidate.Classification,
                        candidate.PositionSource,
                        candidate.Score,
                        DeserializeComponents(candidate.ScoreComponentsJson),
                        DeserializeConstraints(candidate.ConstraintResultsJson),
                        DeserializeExplanations(candidate.ExplanationJson))).ToList(),
                run.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var driverName) ? driverName : null,
                run.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var vehicleRegistration) ? vehicleRegistration : null,
                run.TrailerId is Guid trailerId && trailers.TryGetValue(trailerId, out var trailerNumber) ? trailerNumber : null)).ToList());
    }

    private static IReadOnlyList<PlanProposalWarning> DeserializeWarnings(string value)
    {
        try { return JsonSerializer.Deserialize<List<PlanProposalWarning>>(value, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IReadOnlyList<string> DeserializeExplanations(string value)
    {
        try { return JsonSerializer.Deserialize<List<string>>(value, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IReadOnlyList<PlanningScoreComponent> DeserializeComponents(string value)
    {
        try { return JsonSerializer.Deserialize<List<PlanningScoreComponent>>(value, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IReadOnlyList<PlanningConstraintResult> DeserializeConstraints(string value)
    {
        try { return JsonSerializer.Deserialize<List<PlanningConstraintResult>>(value, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string Hash(
        DateOnly date,
        string period,
        IReadOnlyList<Balance> balances,
        IReadOnlyDictionary<Guid, int> allocated,
        IReadOnlyList<Driver> drivers,
        IReadOnlyList<Trailer> trailers)
    {
        var source = string.Join("|", new[]
        {
            date.ToString("yyyy-MM-dd"),
            period,
            string.Join(";", balances.Select(item => $"{item.Line.Id:N}:{item.Remaining}:{item.Line.CollectionSite}:{item.Line.DeliverySite}:{item.Line.PalletType}")),
            string.Join(";", allocated.OrderBy(item => item.Key).Select(item => $"{item.Key:N}:{item.Value}")),
            string.Join(";", drivers.OrderBy(item => item.Id).Select(item => $"{item.Id:N}:{item.TachoDriveAvailableTodayMinutes}:{item.LastTachoSyncUtc?.ToUnixTimeSeconds()}")),
            string.Join(";", trailers.OrderBy(item => item.Id).Select(item => $"{item.Id:N}:{item.TrailerNumber}:{item.Type}:{item.StandardCapacity}:{item.EuroCapacity}"))
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static string NormalisePeriod(string? value) => string.Equals(value?.Trim(), "PM", StringComparison.OrdinalIgnoreCase) ? "PM" :
        string.Equals(value?.Trim(), "FULL_DAY", StringComparison.OrdinalIgnoreCase) || string.Equals(value?.Trim(), "FULL DAY", StringComparison.OrdinalIgnoreCase) ? "FULL_DAY" : "AM";
    private static string Period(TimeOnly? value) => value is not null && value.Value >= new TimeOnly(17, 0) ? "PM" : "AM";
    private static int Capacity(string? palletType) => palletType?.Contains("euro", StringComparison.OrdinalIgnoreCase) == true ? 33 : 26;
    private static bool IsEuro(string? palletType) => palletType?.Contains("euro", StringComparison.OrdinalIgnoreCase) == true;
    private static Trailer? SelectTrailer(IReadOnlyList<Trailer> trailers, string? palletType, IReadOnlySet<Guid> excludedTrailerIds) => trailers
        .Where(trailer => !excludedTrailerIds.Contains(trailer.Id))
        .Where(trailer => TrailerCapacityOrNull(trailer, palletType) is > 0)
        .OrderByDescending(trailer => TrailerCapacityOrNull(trailer, palletType))
        .ThenBy(trailer => trailer.TrailerNumber, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
    private static int TrailerCapacity(Trailer? trailer, string? palletType) => TrailerCapacityOrNull(trailer, palletType) ?? Capacity(palletType);
    private static int? TrailerCapacityOrNull(Trailer? trailer, string? palletType) => trailer is null
        ? null
        : palletType?.Contains("euro", StringComparison.OrdinalIgnoreCase) == true
            ? trailer.EuroCapacity
            : trailer.StandardCapacity;
    private static void AddLockedRuns(PlanProposal proposal, IReadOnlyList<LoadBaseline> lockedRuns)
    {
        foreach (var baseline in lockedRuns.OrderBy(run => run.Reference, StringComparer.OrdinalIgnoreCase))
        {
            proposal.Runs.Add(new PlanProposalRun
            {
                Sequence = proposal.Runs.Count + 1,
                Reference = baseline.Reference,
                IsLocked = true,
                LiveLoadId = baseline.Id,
                DriverId = baseline.DriverId,
                VehicleId = baseline.VehicleId,
                TrailerId = baseline.TrailerId,
                Classification = "Recommended",
                PositionSource = "LockedPlan",
                CapacityPallets = 0,
                PlannedPallets = 0,
                Score = 0m,
                ScoreComponentsJson = "[]",
                ExplanationJson = JsonSerializer.Serialize(new[] { "Existing locked run retained unchanged as a fixed planning constraint." }, JsonOptions)
            });
        }
    }
    private static Site? MatchSite(IEnumerable<Site> sites, string? name) => string.IsNullOrWhiteSpace(name) ? null : sites
        .OrderBy(site => string.Equals(site.Name, name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .FirstOrDefault(site => string.Equals(site.Name, name, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(site.Aliases) && site.Aliases.Contains(name, StringComparison.OrdinalIgnoreCase)));
    private static Slh.Tms.Api.Models.Tracking.VehicleLiveStatus? MatchLive(Vehicle vehicle, IEnumerable<Slh.Tms.Api.Models.Tracking.VehicleLiveStatus> statuses)
    {
        var keys = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Normalise).ToHashSet();
        return statuses.Where(status => keys.Contains(Normalise(status.VehicleIdentifier))).OrderByDescending(status => status.LastEventTimeUtc).FirstOrDefault();
    }
    private static IReadOnlyList<Driver> CandidateDrivers(
        IReadOnlyList<Driver> drivers,
        IReadOnlySet<Guid> recentDriverIds,
        DateTimeOffset evidenceAt) =>
        drivers
            .OrderByDescending(driver =>
                driver.TachoDriveAvailableTodayMinutes is not null &&
                driver.LastTachoSyncUtc is not null &&
                evidenceAt - driver.LastTachoSyncUtc <= TimeSpan.FromHours(6))
            .ThenByDescending(driver => recentDriverIds.Contains(driver.Id))
            .ThenBy(driver => driver.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(driver => driver.Id)
            .Take(MaxCandidateDriversPerRun)
            .ToList();

    private static IReadOnlyList<Vehicle> CandidateVehicles(
        IReadOnlyList<Vehicle> vehicles,
        IReadOnlyList<Slh.Tms.Api.Models.Tracking.VehicleLiveStatus> liveStatuses,
        IReadOnlySet<Guid> recentVehicleIds,
        DateTimeOffset evidenceAt) =>
        vehicles
            .OrderByDescending(vehicle =>
                MatchLive(vehicle, liveStatuses) is { LastEventTimeUtc: var observedAt } &&
                evidenceAt - observedAt <= TimeSpan.FromMinutes(30))
            .ThenByDescending(vehicle => recentVehicleIds.Contains(vehicle.Id))
            .ThenBy(vehicle => vehicle.Registration, StringComparer.OrdinalIgnoreCase)
            .ThenBy(vehicle => vehicle.Id)
            .Take(MaxCandidateVehiclesPerRun)
            .ToList();

    private static int RequiredDriveMinutes(decimal? collectionLatitude, decimal? deliveryLatitude) => collectionLatitude is null || deliveryLatitude is null
        ? 240
        : Math.Max(60, (int)Math.Ceiling(Math.Abs(collectionLatitude.Value - deliveryLatitude.Value) * 69m / 45m * 60m) + 60);
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
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static DateTimeOffset StartOfDayUtc(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
            message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
    private sealed record Balance(OrderSourceLine Line, int Remaining);
    private sealed record Candidate(Driver Driver, Vehicle Vehicle, PlanningConstraintEvaluation Constraints, PlanningCandidateScore Score);
}
