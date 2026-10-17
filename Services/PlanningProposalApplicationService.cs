using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class PlanningProposalApplicationService(TmsDbContext db)
{
    private const string AllocationType = "planningpalletallocation";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ApplyPlanProposalResult> ApplyAsync(
        Guid proposalId,
        ApplyPlanProposalRequest request,
        string? actor,
        CancellationToken ct)
    {
        var proposal = await db.PlanProposals
            .Include(item => item.Runs).ThenInclude(run => run.Allocations)
            .SingleOrDefaultAsync(item => item.Id == proposalId, ct)
            ?? throw new PlanProposalApplyException("ProposalNotFound", "The planning proposal no longer exists.");

        if (!string.Equals(proposal.Status, "Generated", StringComparison.OrdinalIgnoreCase))
            throw new PlanProposalApplyException("ProposalAlreadyFinalised", $"Proposal status is {proposal.Status}; only Generated proposals can be applied.");

        var generatedRuns = proposal.Runs.Where(run => !run.IsLocked).OrderBy(run => run.Sequence).ToList();
        if (string.Equals(proposal.Classification, "Blocked", StringComparison.OrdinalIgnoreCase) ||
            generatedRuns.Any(run => string.Equals(run.Classification, "Blocked", StringComparison.OrdinalIgnoreCase)))
            throw new PlanProposalApplyException("ProposalBlocked", "Blocked optimiser work cannot be applied to live planning.");

        var unverified = string.Equals(proposal.Classification, "Unverified", StringComparison.OrdinalIgnoreCase) ||
            generatedRuns.Any(run => string.Equals(run.Classification, "Unverified", StringComparison.OrdinalIgnoreCase));
        if (unverified && !request.AcknowledgeUnverified)
            throw new PlanProposalApplyException("UnverifiedAcknowledgementRequired", "This proposal contains Unverified evidence and requires explicit planner acknowledgement before application.");

        var betaRouteBuild = IsBetaRouteBuild(proposal);
        if (!betaRouteBuild && generatedRuns.Any(run => run.DriverId is null || run.VehicleId is null))
            throw new PlanProposalApplyException("ProposalIncomplete", "Every new optimiser run must have a selected driver and vehicle before it can be applied.");

        var lockedLoadIds = proposal.Runs
            .Where(run => run.IsLocked && run.LiveLoadId is not null)
            .Select(run => run.LiveLoadId!.Value)
            .ToHashSet();
        var existingLoads = await db.Loads.AsNoTracking()
            .Where(load => load.PlanningDate == proposal.PlanningDate && load.Status != LoadStatus.Cancelled)
            .ToListAsync(ct);
        var generatedReferences = generatedRuns.Select(run => LiveReference(proposal, run)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (existingLoads.Any(load => !lockedLoadIds.Contains(load.Id) && generatedReferences.Contains(load.Reference)))
            throw new PlanProposalApplyException("LivePlanChanged", "A live run now uses a reference required by this proposal. Generate a fresh proposal before applying.");

        foreach (var run in generatedRuns)
        {
            var conflict = existingLoads.FirstOrDefault(load => !lockedLoadIds.Contains(load.Id) &&
                ((run.DriverId is not null && load.DriverId == run.DriverId) ||
                 (run.VehicleId is not null && load.VehicleId == run.VehicleId) ||
                 (run.TrailerId is not null && load.TrailerId == run.TrailerId)));
            if (conflict is not null)
                throw new PlanProposalApplyException("LiveResourceConflict", $"Live planning changed after proposal generation; run {conflict.Reference} now uses a selected driver, vehicle or trailer.");
        }

        await VerifyPalletBalancesAsync(proposal, generatedRuns, betaRouteBuild, ct);

        var created = new List<Guid>();
        foreach (var run in generatedRuns)
        {
            var load = new Load
            {
                Reference = LiveReference(proposal, run),
                PlanningDate = proposal.PlanningDate,
                Status = LoadStatus.Draft,
                DriverId = run.DriverId,
                VehicleId = run.VehicleId,
                TrailerId = run.TrailerId,
                PalletSpacesUsed = run.PlannedPallets,
                TotalPalletSpaces = run.CapacityPallets,
                CapacityType = run.Allocations.Any(allocation => IsEuro(allocation.PalletType)) ? "Euro pallets" : "Standard pallets",
                PlannerNotes = betaRouteBuild
                    ? $"Built by Beta Optimiser proposal {proposal.Id:N} version {proposal.Version}. Route approved into Runs; resource allocation remains in Driver Dispatch and manual planner changes are authoritative."
                    : $"Applied from optimiser proposal {proposal.Id:N} version {proposal.Version}; planner approval required before dispatch."
            };

            foreach (var allocation in run.Allocations.OrderBy(item => item.CollectionSequence).ThenBy(item => item.CollectionSite))
            {
                load.Stops.Add(new LoadStop
                {
                    LoadId = load.Id,
                    Sequence = allocation.CollectionSequence,
                    Name = $"Collect · {allocation.CollectionSite ?? "Unspecified collection"}"
                });
            }
            foreach (var allocation in run.Allocations.OrderBy(item => item.DeliverySequence).ThenBy(item => item.DeliverySite))
            {
                load.Stops.Add(new LoadStop
                {
                    LoadId = load.Id,
                    Sequence = allocation.DeliverySequence,
                    Name = $"Deliver · {allocation.DeliverySite ?? "Unspecified delivery"}"
                });
            }

            db.Loads.Add(load);
            created.Add(load.Id);

            foreach (var allocation in run.Allocations)
            {
                db.StagedImports.Add(new StagedImport
                {
                    EntityType = AllocationType,
                    IdempotencyKey = $"optimiserapply:{proposal.Id:N}:{allocation.Id:N}",
                    Source = betaRouteBuild ? "Planner-approved Beta route proposal" : "Planner-approved optimiser proposal",
                    Status = StagingStatus.Promoted,
                    ReviewedAtUtc = DateTimeOffset.UtcNow,
                    ReviewedBy = actor,
                    ReviewNote = betaRouteBuild
                        ? $"Route copied into Draft Runs from Beta proposal version {proposal.Version}; Dispatch resource allocation remains outstanding."
                        : $"Applied from optimiser proposal version {proposal.Version}.",
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        sourceLineId = allocation.SourceLineId,
                        loadId = load.Id,
                        date = proposal.PlanningDate,
                        pallets = allocation.Pallets,
                        proposalId = proposal.Id,
                        proposalRunId = run.Id
                    }, JsonOptions)
                });
            }
        }

        proposal.Status = "Applied";
        await db.SaveChangesAsync(ct);

        var warnings = new List<string>();
        if (unverified) warnings.Add("Planner explicitly acknowledged Unverified evidence before application.");
        if (betaRouteBuild) warnings.Add("Beta routes were created as Draft Runs. Driver, vehicle and trailer allocation remains in Driver Dispatch/manual planning.");
        return new ApplyPlanProposalResult(proposal.Id, proposal.Status, created.Count, created, warnings);
    }

    private async Task VerifyPalletBalancesAsync(
        PlanProposal proposal,
        IReadOnlyList<PlanProposalRun> runs,
        bool betaRouteBuild,
        CancellationToken ct)
    {
        var requested = runs.SelectMany(run => run.Allocations)
            .GroupBy(allocation => allocation.SourceLineId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Pallets));
        if (requested.Count == 0) return;

        var sourceIds = requested.Keys.ToList();
        var sourceLines = await db.OrderSourceLines.AsNoTracking().Where(line => sourceIds.Contains(line.Id)).ToListAsync(ct);
        if (sourceLines.Count != sourceIds.Count)
            throw new PlanProposalApplyException("SourceEvidenceChanged", "One or more proposal source lines are no longer available.");

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
                if (!root.TryGetProperty("sourceLineId", out var source) || source.ValueKind != JsonValueKind.String || !source.TryGetGuid(out var sourceLineId) || !requested.ContainsKey(sourceLineId)) continue;
                if (root.TryGetProperty("date", out var dateElement) && DateOnly.TryParse(dateElement.GetString(), out var allocationDate) && allocationDate != proposal.PlanningDate) continue;
                var loadId = root.TryGetProperty("loadId", out var load) ? load.ToString() : string.Empty;
                var key = $"{sourceLineId:N}:{loadId}";
                if (latest.ContainsKey(key)) continue;
                var pallets = root.TryGetProperty("pallets", out var quantity) && quantity.TryGetInt32(out var value) ? Math.Max(value, 0) : 0;
                latest[key] = (sourceLineId, pallets);
            }
            catch (JsonException) { }
        }
        var allocated = latest.Values.GroupBy(item => item.SourceLineId).ToDictionary(group => group.Key, group => group.Sum(item => item.Pallets));

        foreach (var line in sourceLines)
        {
            var effectiveDate = betaRouteBuild ? line.CollectionDate ?? line.DeliveryDate : line.CollectionDate;
            if (effectiveDate != proposal.PlanningDate)
                throw new PlanProposalApplyException("SourceEvidenceChanged", "A proposal source line changed after proposal generation.");

            var requestedPallets = requested[line.Id];
            if (line.Pallets is null)
            {
                if (betaRouteBuild && requestedPallets == 0) continue;
                throw new PlanProposalApplyException("SourceEvidenceChanged", "A proposal source line no longer has the quantity used by the optimiser.");
            }
            if (line.Pallets < 0)
                throw new PlanProposalApplyException("SourceEvidenceChanged", "A proposal source line changed after proposal generation.");
            if (allocated.GetValueOrDefault(line.Id) + requestedPallets > line.Pallets.Value)
                throw new PlanProposalApplyException("PalletConflict", "Live pallet allocations changed after proposal generation. Generate a fresh proposal before applying.");
        }
    }

    private static bool IsBetaRouteBuild(PlanProposal proposal)
    {
        try
        {
            using var document = JsonDocument.Parse(proposal.EvidenceJson);
            return document.RootElement.TryGetProperty("proposalMode", out var mode) &&
                   string.Equals(mode.GetString(), BetaPlanProposalService.ProposalMode, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string LiveReference(PlanProposal proposal, PlanProposalRun run) =>
        IsBetaRouteBuild(proposal)
            ? run.Reference
            : $"RUN-{proposal.PlanningDate:yyyyMMdd}-{proposal.Period}-{run.Sequence:00}";

    private static bool IsEuro(string? palletType) => palletType?.Contains("euro", StringComparison.OrdinalIgnoreCase) == true;
}
