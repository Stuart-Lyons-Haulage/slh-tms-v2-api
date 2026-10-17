using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Converts the independent Beta day-plan into an immutable, reviewable planning proposal.
/// The proposal contains route/order work only: driver/vehicle/trailer selection remains in Dispatch.
/// Existing promoted pallet allocations are never copied into a new Beta route proposal.
/// </summary>
public sealed class BetaPlanProposalService(TmsDbContext db, ILogger<BetaPlanProposalService> logger)
{
    public const string ProposalMode = "BetaRouteBuild";
    private const string AllocationType = "planningpalletallocation";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public async Task<PlanProposalResult> GenerateAsync(
        DateOnly planningDate,
        BetaDayPlanService beta,
        string? actor,
        CancellationToken ct)
    {
        var built = await beta.BuildDayAsync(planningDate, ct);
        var touchedSourceLines = await AllocatedSourceLinesAsync(planningDate, ct);
        var warnings = new List<PlanProposalWarning>();

        foreach (var warning in built.Warnings)
            warnings.Add(new PlanProposalWarning("BetaRouteEvidence", "Warning", warning));

        if (touchedSourceLines.Count > 0)
            warnings.Add(new PlanProposalWarning(
                "ExistingPlanningPreserved",
                "Info",
                "Orders already allocated to a live/draft run are locked out of this proposal. Re-optimisation only uses currently unplanned work."));

        var candidateRuns = new List<(BetaDayPlanRunResult Run, List<BetaDayPlanOrderResult> Orders, List<string> Stops)>();
        foreach (var run in built.Runs)
        {
            var remainingOrders = run.Orders.Where(order => !touchedSourceLines.Contains(order.SourceLineId)).ToList();
            if (remainingOrders.Count == 0) continue;

            var relevantNames = remainingOrders
                .SelectMany(order => new[] { order.CollectionName, order.DeliveryName })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stops = StopNames(run).Where(stop => relevantNames.Contains(stop)).ToList();
            if (stops.Count == 0)
                stops = remainingOrders.SelectMany(order => new[] { order.CollectionName, order.DeliveryName }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            candidateRuns.Add((run, remainingOrders, stops));
        }

        if (candidateRuns.Count == 0)
            warnings.Add(new PlanProposalWarning(
                "NoUnplannedWork",
                "Info",
                "There is no unplanned PlannerReady order work to copy into Runs for this date."));

        var evidenceAt = DateTimeOffset.UtcNow;
        var classification = candidateRuns.Any(item => !item.Run.RoutingAvailable) || built.UnmappedOrderLines > 0
            ? "Unverified"
            : "Recommended";
        if (candidateRuns.Count == 0) classification = "Unverified";

        var nextVersion = (await db.PlanProposals.AsNoTracking()
            .Where(item => item.PlanningDate == planningDate && item.Period == "FULL_DAY")
            .MaxAsync(item => (int?)item.Version, ct) ?? 0) + 1;

        var proposal = new PlanProposal
        {
            PlanningDate = planningDate,
            Period = "FULL_DAY",
            Version = nextVersion,
            Status = "Generated",
            Classification = classification,
            InputHash = InputHash(planningDate, candidateRuns),
            EvidenceCapturedAtUtc = evidenceAt,
            CreatedAtUtc = evidenceAt,
            CreatedBy = actor,
            EvidenceJson = JsonSerializer.Serialize(new
            {
                proposalMode = ProposalMode,
                capturedAtUtc = evidenceAt,
                betaGeneratedAtUtc = built.GeneratedAtUtc,
                source = "Beta Optimiser day-plan",
                routePolicy = built.RoutingPolicy,
                unplannedOnly = true,
                resourceAssignment = "Dispatch",
                eligibleOrderLines = candidateRuns.SelectMany(item => item.Orders).Select(order => order.SourceLineId).Distinct().Count(),
                preservedAllocatedSourceLines = touchedSourceLines.Count,
                routingComplete = candidateRuns.Count > 0 && candidateRuns.All(item => item.Run.RoutingAvailable)
            }, JsonOptions),
            WarningsJson = JsonSerializer.Serialize(warnings, JsonOptions)
        };

        var sequence = 0;
        foreach (var item in candidateRuns)
        {
            sequence++;
            var runClassification = item.Run.RoutingAvailable ? "Recommended" : "Unverified";
            var proposalRun = new PlanProposalRun
            {
                Sequence = sequence,
                Reference = $"RUN {sequence} {NormalisePeriod(item.Run.Period)}",
                IsLocked = false,
                Classification = runClassification,
                CapacityPallets = item.Run.CapacityPallets,
                PlannedPallets = item.Run.PlannedPallets,
                Score = item.Run.UtilisationPercent,
                ScoreComponentsJson = JsonSerializer.Serialize(new[]
                {
                    new PlanningScoreComponent("Utilisation", item.Run.UtilisationPercent, $"{item.Run.PlannedPallets}/{item.Run.CapacityPallets} planned pallet capacity."),
                    new PlanningScoreComponent("HgvRoute", item.Run.RoutingAvailable ? 10m : -10m, item.Run.RoutingAvailable
                        ? $"Live HGV route: {item.Run.Miles:0.0} mi / {item.Run.DriveMinutes ?? 0} min."
                        : "Live HGV route evidence is incomplete; planner acknowledgement is required.")
                }, JsonOptions),
                ExplanationJson = JsonSerializer.Serialize(
                    new[]
                    {
                        "Built from currently unplanned TMS orders by the Beta Optimiser.",
                        "Route sequence is preserved from the Beta HGV optimiser; driver, vehicle and trailer are intentionally left for Dispatch.",
                        "Submitting creates Draft Runs only. Manual planner changes remain authoritative."
                    }.Concat(item.Run.Warnings).ToList(), JsonOptions)
            };

            foreach (var order in item.Orders)
            {
                var collectionSequence = SequenceFor(item.Stops, order.CollectionName, preferLast: false);
                var deliverySequence = SequenceFor(item.Stops, order.DeliveryName, preferLast: true);
                if (deliverySequence <= collectionSequence)
                    deliverySequence = Math.Max(item.Stops.Count + 1, collectionSequence + 1);

                proposalRun.Allocations.Add(new PlanProposalAllocation
                {
                    SourceLineId = order.SourceLineId,
                    Pallets = Math.Max(order.Pallets, 0),
                    PalletType = order.PalletType,
                    CollectionSite = order.CollectionName,
                    DeliverySite = order.DeliveryName,
                    CollectionSequence = collectionSequence,
                    DeliverySequence = deliverySequence
                });
            }

            proposal.Runs.Add(proposalRun);
        }

        db.PlanProposals.Add(proposal);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Generated Beta route-build proposal {ProposalId} for {PlanningDate} with {RunCount} unplanned runs.",
            proposal.Id, planningDate, proposal.Runs.Count);

        return ToResult(proposal, warnings);
    }

    private async Task<HashSet<Guid>> AllocatedSourceLinesAsync(DateOnly date, CancellationToken ct)
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

        return latest.Values
            .GroupBy(item => item.SourceLineId)
            .Where(group => group.Sum(item => item.Pallets) > 0)
            .Select(group => group.Key)
            .ToHashSet();
    }

    private static List<string> StopNames(BetaDayPlanRunResult run)
    {
        var result = new List<string>();
        foreach (var raw in run.Stops)
        {
            try
            {
                var element = raw is JsonElement json ? json : JsonSerializer.SerializeToElement(raw, JsonOptions);
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("name", out var name))
                {
                    var value = name.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
                }
            }
            catch (JsonException) { }
        }
        return result;
    }

    private static int SequenceFor(IReadOnlyList<string> stops, string name, bool preferLast)
    {
        var matches = stops
            .Select((stop, index) => (Stop: stop, Sequence: index + 1))
            .Where(item => string.Equals(item.Stop.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase) || Normalise(item.Stop) == Normalise(name))
            .Select(item => item.Sequence)
            .ToList();
        if (matches.Count == 0) return preferLast ? stops.Count + 1 : 1;
        return preferLast ? matches[^1] : matches[0];
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string NormalisePeriod(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "PM" => "PM",
        "W3" => "W3",
        _ => "AM"
    };

    private static string InputHash(DateOnly date, IEnumerable<(BetaDayPlanRunResult Run, List<BetaDayPlanOrderResult> Orders, List<string> Stops)> runs)
    {
        var raw = new StringBuilder(date.ToString("yyyy-MM-dd"));
        foreach (var item in runs)
        {
            raw.Append('|').Append(item.Run.Period).Append('|').Append(item.Run.CapacityPallets);
            foreach (var order in item.Orders.OrderBy(order => order.SourceLineId))
                raw.Append('|').Append(order.SourceLineId.ToString("N")).Append(':').Append(order.Pallets);
            foreach (var stop in item.Stops) raw.Append("->").Append(Normalise(stop));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw.ToString())));
    }

    private static PlanProposalResult ToResult(PlanProposal proposal, IReadOnlyList<PlanProposalWarning> warnings) => new(
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
            Deserialize<List<PlanningScoreComponent>>(run.ScoreComponentsJson) ?? [],
            Deserialize<List<string>>(run.ExplanationJson) ?? [],
            run.Allocations.OrderBy(allocation => allocation.CollectionSequence).Select(allocation => new PlanProposalAllocationResult(
                allocation.Id,
                allocation.SourceLineId,
                allocation.Pallets,
                allocation.PalletType,
                allocation.CollectionSite,
                allocation.DeliverySite,
                allocation.CollectionSequence,
                allocation.DeliverySequence)).ToList(),
            [],
            null,
            null,
            null)).ToList());

    private static T? Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return default; }
    }
}
