namespace Slh.Tms.Api.Services;

public sealed record BetaRouteInputStop(
    Guid StopId,
    Guid? OrderId,
    string Name,
    decimal Latitude,
    decimal Longitude);

public sealed record BetaRouteInput(
    Guid LoadId,
    string Reference,
    bool IsProtected,
    IReadOnlyList<BetaRouteInputStop> Stops);

public sealed record BetaRouteAnalysis(
    Guid LoadId,
    string Reference,
    bool IsProtected,
    bool RoutingAvailable,
    string RoutingSource,
    decimal? CurrentMiles,
    int? CurrentDriveMinutes,
    decimal? ProposedMiles,
    int? ProposedDriveMinutes,
    decimal? SavingMiles,
    int? SavingDriveMinutes,
    IReadOnlyList<BetaRouteInputStop> Before,
    IReadOnlyList<BetaRouteInputStop> After,
    string Rationale);

/// <summary>
/// Read-only route analyser for the Beta Optimiser. It evaluates a deliberately
/// bounded set of adjacent swaps using Azure Maps HGV evidence and never changes
/// operational loads. Order-linked stop precedence from the live plan is preserved.
/// </summary>
public sealed class BetaRouteOptimisationEngine(IBetaHgvRouteProvider routeProvider)
{
    private const int MaxCandidateSwaps = 16;
    private const decimal MinimumMileageSaving = 0.5m;
    private const int MinimumTimeSavingMinutes = 5;

    public async Task<BetaRouteAnalysis> AnalyseAsync(BetaRouteInput input, CancellationToken ct)
    {
        var before = input.Stops.ToList();
        if (before.Count < 2)
        {
            return new BetaRouteAnalysis(
                input.LoadId,
                input.Reference,
                input.IsProtected,
                false,
                "Unavailable",
                null,
                null,
                null,
                null,
                null,
                null,
                before,
                before,
                "At least two mapped stops are required before Azure Maps HGV routing can analyse this run.");
        }

        var baseline = await routeProvider.GetRouteAsync(ToPoints(before), ct);
        if (baseline is null)
        {
            return new BetaRouteAnalysis(
                input.LoadId,
                input.Reference,
                input.IsProtected,
                false,
                "Unavailable",
                null,
                null,
                null,
                null,
                null,
                null,
                before,
                before,
                "Azure Maps HGV routing evidence is unavailable, so the Beta Optimiser will not estimate or recommend a route change.");
        }

        if (input.IsProtected)
        {
            return CurrentOnly(input, before, baseline,
                $"Azure Maps HGV routing measured the live route at {baseline.Miles:0.#} miles / {baseline.DriveMinutes} minutes. The run is operationally protected, so no resequencing is suggested.");
        }

        if (before.Count < 3)
        {
            return CurrentOnly(input, before, baseline,
                $"Azure Maps HGV routing measured the route at {baseline.Miles:0.#} miles / {baseline.DriveMinutes} minutes. Fewer than three stops leaves no safe resequencing candidate to test.");
        }

        var originalOrderByStop = before
            .Select((stop, index) => (stop.StopId, index))
            .ToDictionary(item => item.StopId, item => item.index);

        IReadOnlyList<BetaRouteInputStop>? bestStops = null;
        BetaHgvRouteCost? bestCost = null;

        var candidateCount = 0;
        for (var index = 0; index < before.Count - 1 && candidateCount < MaxCandidateSwaps; index++)
        {
            var candidate = before.ToList();
            (candidate[index], candidate[index + 1]) = (candidate[index + 1], candidate[index]);

            if (!PreservesOrderPairPrecedence(before, candidate, originalOrderByStop))
                continue;

            candidateCount++;
            var candidateCost = await routeProvider.GetRouteAsync(ToPoints(candidate), ct);
            if (candidateCost is null) continue;

            if (bestCost is null || IsBetter(candidateCost, bestCost))
            {
                bestCost = candidateCost;
                bestStops = candidate;
            }
        }

        if (bestCost is null || bestStops is null)
            return CurrentOnly(input, before, baseline,
                $"Azure Maps HGV routing measured the route at {baseline.Miles:0.#} miles / {baseline.DriveMinutes} minutes. No alternative could be proven with live HGV routing evidence.");

        var savingMiles = baseline.Miles - bestCost.Miles;
        var savingMinutes = baseline.DriveMinutes - bestCost.DriveMinutes;
        var materialSaving = savingMiles >= MinimumMileageSaving || savingMinutes >= MinimumTimeSavingMinutes;
        var doesNotWorsenEither = bestCost.Miles <= baseline.Miles && bestCost.DriveMinutes <= baseline.DriveMinutes;

        if (!materialSaving || !doesNotWorsenEither)
            return CurrentOnly(input, before, baseline,
                $"Azure Maps HGV routing measured the route at {baseline.Miles:0.#} miles / {baseline.DriveMinutes} minutes. Tested alternatives did not produce a material saving without worsening another route metric.");

        return new BetaRouteAnalysis(
            input.LoadId,
            input.Reference,
            input.IsProtected,
            true,
            "AzureMapsHgv",
            baseline.Miles,
            baseline.DriveMinutes,
            bestCost.Miles,
            bestCost.DriveMinutes,
            Math.Round(savingMiles, 2),
            savingMinutes,
            before,
            bestStops,
            $"Azure Maps HGV routing proves this stop order can save approximately {savingMiles:0.#} miles and {savingMinutes} drive minutes while preserving linked-order stop precedence.");
    }

    private static BetaRouteAnalysis CurrentOnly(
        BetaRouteInput input,
        IReadOnlyList<BetaRouteInputStop> before,
        BetaHgvRouteCost baseline,
        string rationale) =>
        new(
            input.LoadId,
            input.Reference,
            input.IsProtected,
            true,
            "AzureMapsHgv",
            baseline.Miles,
            baseline.DriveMinutes,
            null,
            null,
            null,
            null,
            before,
            before,
            rationale);

    private static IReadOnlyList<BetaRoutePoint> ToPoints(IReadOnlyList<BetaRouteInputStop> stops) =>
        stops.Select(stop => new BetaRoutePoint(stop.Name, stop.Latitude, stop.Longitude)).ToList();

    private static bool IsBetter(BetaHgvRouteCost candidate, BetaHgvRouteCost currentBest)
    {
        if (candidate.Miles < currentBest.Miles - 0.1m) return true;
        if (Math.Abs(candidate.Miles - currentBest.Miles) <= 0.1m && candidate.DriveMinutes < currentBest.DriveMinutes) return true;
        return false;
    }

    internal static bool PreservesOrderPairPrecedence(
        IReadOnlyList<BetaRouteInputStop> original,
        IReadOnlyList<BetaRouteInputStop> candidate,
        IReadOnlyDictionary<Guid, int>? originalOrderByStop = null)
    {
        var originalPositions = originalOrderByStop ?? original
            .Select((stop, index) => (stop.StopId, index))
            .ToDictionary(item => item.StopId, item => item.index);
        var candidatePositions = candidate
            .Select((stop, index) => (stop.StopId, index))
            .ToDictionary(item => item.StopId, item => item.index);

        foreach (var group in original.Where(stop => stop.OrderId.HasValue).GroupBy(stop => stop.OrderId!.Value))
        {
            var orderedStops = group.OrderBy(stop => originalPositions[stop.StopId]).ToList();
            for (var index = 1; index < orderedStops.Count; index++)
            {
                if (candidatePositions[orderedStops[index - 1].StopId] > candidatePositions[orderedStops[index].StopId])
                    return false;
            }
        }

        return true;
    }
}
