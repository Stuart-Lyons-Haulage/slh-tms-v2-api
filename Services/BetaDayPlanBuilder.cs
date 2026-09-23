namespace Slh.Tms.Api.Services;

public sealed record BetaDayOrderInput(
    Guid OrderId,
    Guid SourceLineId,
    string Reference,
    string CustomerCode,
    string Period,
    string? PalletType,
    int Pallets,
    TimeOnly? CollectionTimeFrom,
    BetaRoutePoint Collection,
    BetaRoutePoint Delivery,
    bool RoutingMapped = true,
    string? MappingWarning = null,
    TimeOnly? CollectionTimeTo = null,
    TimeOnly? DeliveryDeadline = null,
    string? TimingRule = null);

public sealed record BetaDayBuiltRun(
    string Reference,
    string Period,
    string PalletFamily,
    int CapacityPallets,
    int PlannedPallets,
    decimal UtilisationPercent,
    bool RoutingAvailable,
    decimal? Miles,
    int? DriveMinutes,
    IReadOnlyList<BetaDayOrderInput> Orders,
    IReadOnlyList<BetaRoutePoint> Stops,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds an independent read-only plan directly from order work. Quantified outbound work is
/// capacity planned first. Southbound work is then treated as return/backhaul work and is offered
/// to compatible outbound runs after their northern delivery phase. It is only left standalone
/// when live HGV evidence cannot support a sensible return pairing. Unknown quantities remain
/// visible without inventing pallet capacity.
/// </summary>
public sealed class BetaDayPlanBuilder(
    IBetaHgvRouteProvider routeProvider,
    BetaOptimiserOptions? optimiserOptions = null)
{
    private const int StandardCapacity = 26;
    private const int EuroCapacity = 33;
    private const int MaxCandidateRouteChecksPerFill = 16;
    private const int MaxPhaseSwapChecks = 12;
    private readonly BetaOptimiserOptions options = (optimiserOptions ?? new BetaOptimiserOptions()).Validate();

    public async Task<IReadOnlyList<BetaDayBuiltRun>> BuildAsync(
        DateOnly planningDate,
        IReadOnlyList<BetaDayOrderInput> orders,
        CancellationToken ct)
    {
        var backhaulCandidates = orders
            .Where(order => IsSouthboundBackhaulCandidate(order, options.MinSouthboundLatitudeDelta))
            .OrderBy(order => PeriodRank(order.Period))
            .ThenBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var backhaulIds = backhaulCandidates.Select(order => order.SourceLineId).ToHashSet();
        var primaryOrders = orders.Where(order => !backhaulIds.Contains(order.SourceLineId)).ToList();

        var quantified = ExpandOversizeOrders(primaryOrders.Where(order => order.Pallets > 0).ToList())
            .OrderBy(order => PeriodRank(order.Period))
            .ThenBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unquantified = primaryOrders
            .Where(order => order.Pallets <= 0)
            .OrderBy(order => PeriodRank(order.Period))
            .ThenBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<BetaDayBuiltRun>();
        var sequence = 0;

        foreach (var group in quantified.GroupBy(order => (Period: NormalisePeriod(order.Period), Family: PalletFamily(order.PalletType))))
        {
            var remaining = group.ToList();
            while (remaining.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var capacity = Capacity(group.Key.Family);
                var selected = new List<BetaDayOrderInput> { remaining[0] };
                remaining.RemoveAt(0);
                var planned = selected[0].Pallets;

                while (remaining.Count > 0 && planned < capacity)
                {
                    var fits = remaining.Where(order => planned + order.Pallets <= capacity).Take(MaxCandidateRouteChecksPerFill).ToList();
                    if (fits.Count == 0) break;

                    BetaDayOrderInput? best = null;
                    BetaHgvRouteCost? bestCost = null;
                    var routeValidatedCandidate = false;
                    foreach (var candidate in fits)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!selected.All(order => order.RoutingMapped) || !candidate.RoutingMapped) continue;
                        var cost = await routeProvider.GetRouteAsync(BuildStops(selected.Append(candidate).ToList()), ct);
                        if (cost is null) continue;
                        routeValidatedCandidate = true;
                        var candidateOrders = selected.Append(candidate).ToList();
                        if (!MeetsTimingWindow(planningDate, candidateOrders, cost, options) ||
                            !MeetsOperationalLimits(BuildStops(candidateOrders).Count, cost, options)) continue;
                        if (bestCost is null || Better(cost, bestCost))
                        {
                            best = candidate;
                            bestCost = cost;
                        }
                    }

                    if (best is null && routeValidatedCandidate) break;
                    best ??= fits
                        .OrderBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
                        .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
                        .First();

                    selected.Add(best);
                    remaining.Remove(best);
                    planned += best.Pallets;
                }

                sequence++;
                result.Add(await BuildPrimaryRunAsync(planningDate, group.Key.Period, group.Key.Family, sequence, selected, ct));
            }
        }

        foreach (var order in unquantified)
        {
            ct.ThrowIfCancellationRequested();
            sequence++;
            result.Add(await BuildStandaloneMovementAsync(planningDate, sequence, order, false, ct));
        }

        foreach (var backhaul in backhaulCandidates)
        {
            ct.ThrowIfCancellationRequested();
            var attached = await TryAttachBackhaulAsync(result, backhaul, ct);
            if (attached) continue;
            sequence++;
            result.Add(await BuildStandaloneMovementAsync(planningDate, sequence, backhaul, true, ct));
        }

        return result
            .OrderBy(run => PeriodRank(run.Period))
            .ThenBy(run => run.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<BetaDayBuiltRun> BuildPrimaryRunAsync(
        DateOnly planningDate,
        string period,
        string family,
        int sequence,
        IReadOnlyList<BetaDayOrderInput> selected,
        CancellationToken ct)
    {
        var capacity = Capacity(family);
        var planned = selected.Sum(order => order.Pallets);
        var warnings = selected
            .Where(order => !order.RoutingMapped || !string.IsNullOrWhiteSpace(order.MappingWarning))
            .Select(order => order.MappingWarning ?? $"{order.Reference}: collection or delivery is not mapped to Site Master coordinates.")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyList<BetaRoutePoint> stops = BuildStops(selected);
        BetaHgvRouteCost? route = null;
        if (selected.All(order => order.RoutingMapped))
        {
            var collectionCount = DistinctPoints(selected
                .OrderBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
                .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
                .Select(order => order.Collection)).Count;
            (stops, route) = await OptimiseWithinCollectionAndDeliveryPhasesAsync(stops, collectionCount, ct);
            route ??= await routeProvider.GetRouteAsync(stops, ct);
        }

        if (route is null)
            warnings.Add("Live Azure Maps HGV evidence is unavailable for this run. It remains in the Beta day plan but no estimated mileage is substituted.");
        else if (!MeetsTimingWindow(planningDate, selected, route, options) ||
                 !MeetsOperationalLimits(stops.Count, route, options))
            warnings.Add("Site Master access/depot deadline cannot be met by the current route timing; planner review is required.");

        return new BetaDayBuiltRun(
            $"BETA-{planningDate:yyyyMMdd}-{period}-{sequence:00}",
            period,
            family,
            capacity,
            planned,
            Math.Round((decimal)planned / capacity * 100m, 1),
            route is not null,
            route?.Miles,
            route?.DriveMinutes,
            selected,
            stops,
            warnings);
    }

    private async Task<BetaDayBuiltRun> BuildStandaloneMovementAsync(
        DateOnly planningDate,
        int sequence,
        BetaDayOrderInput order,
        bool isUnpairedBackhaul,
        CancellationToken ct)
    {
        var family = order.Pallets > 0 ? PalletFamily(order.PalletType) : "Unquantified";
        var capacity = order.Pallets > 0 ? Capacity(family) : 0;
        var warnings = new List<string>();
        if (order.Pallets <= 0)
            warnings.Add("Quantity was not stated for this movement. It is routed and reconciled, but excluded from pallet-capacity utilisation.");
        if (isUnpairedBackhaul)
            warnings.Add("Southbound movement could not be safely paired to a compatible returning outbound run using live HGV evidence, so it remains standalone for planner review.");
        if (!order.RoutingMapped || !string.IsNullOrWhiteSpace(order.MappingWarning))
            warnings.Add(order.MappingWarning ?? $"{order.Reference}: collection or delivery is not mapped to Site Master coordinates.");

        var stops = BuildStops([order]);
        BetaHgvRouteCost? route = null;
        if (order.RoutingMapped && stops.Count >= 2)
            route = await routeProvider.GetRouteAsync(stops, ct);
        if (route is null)
            warnings.Add("Live Azure Maps HGV evidence is unavailable for this movement. No approximate mileage was substituted.");

        return new BetaDayBuiltRun(
            $"BETA-{planningDate:yyyyMMdd}-{NormalisePeriod(order.Period)}-{sequence:00}",
            NormalisePeriod(order.Period),
            family,
            capacity,
            Math.Max(order.Pallets, 0),
            capacity == 0 ? 0m : Math.Round((decimal)order.Pallets / capacity * 100m, 1),
            route is not null,
            route?.Miles,
            route?.DriveMinutes,
            [order],
            stops,
            warnings);
    }

    private async Task<bool> TryAttachBackhaulAsync(List<BetaDayBuiltRun> runs, BetaDayOrderInput backhaul, CancellationToken ct)
    {
        if (!backhaul.RoutingMapped || NormalisePeriod(backhaul.Period) == "W3") return false;
        var backhaulFamily = backhaul.Pallets > 0 ? PalletFamily(backhaul.PalletType) : null;
        var standalone = await routeProvider.GetRouteAsync([backhaul.Collection, backhaul.Delivery], ct);
        if (standalone is null) return false;

        var bestIndex = -1;
        BetaHgvRouteCost? bestCombined = null;
        decimal bestIncrement = decimal.MaxValue;

        for (var index = 0; index < runs.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var run = runs[index];
            if (!run.RoutingAvailable || run.Miles is null) continue;
            if (NormalisePeriod(run.Period) != NormalisePeriod(backhaul.Period) || run.Period == "W3") continue;
            if (run.Stops.Count < 2) continue;
            if (backhaulFamily is not null && !string.Equals(run.PalletFamily, backhaulFamily, StringComparison.OrdinalIgnoreCase)) continue;
            if (backhaul.Pallets > 0 && backhaul.Pallets > run.CapacityPallets) continue;

            var terminal = run.Stops[^1];
            if (backhaul.Collection.Latitude > terminal.Latitude + options.MaxNorthwardBackhaulDetourLatitude) continue;
            if (backhaul.Collection.Latitude - backhaul.Delivery.Latitude < options.MinSouthboundLatitudeDelta) continue;

            var combinedStops = AppendDistinct(run.Stops, backhaul.Collection, backhaul.Delivery);
            var combined = await routeProvider.GetRouteAsync(combinedStops, ct);
            if (combined is null) continue;
            var increment = Math.Max(combined.Miles - run.Miles.Value, 0m);
            if (increment > standalone.Miles * options.MaxBackhaulIncrementFactor + options.BackhaulIncrementAllowanceMiles) continue;
            if (increment >= bestIncrement) continue;
            bestIndex = index;
            bestCombined = combined;
            bestIncrement = increment;
        }

        if (bestIndex < 0 || bestCombined is null) return false;

        var selectedRun = runs[bestIndex];
        var updatedStops = AppendDistinct(selectedRun.Stops, backhaul.Collection, backhaul.Delivery);
        var peakPallets = backhaul.Pallets > 0 ? Math.Max(selectedRun.PlannedPallets, backhaul.Pallets) : selectedRun.PlannedPallets;
        var warnings = selectedRun.Warnings
            .Where(warning => !warning.StartsWith("Live Azure Maps HGV evidence is unavailable", StringComparison.OrdinalIgnoreCase))
            .ToList();
        warnings.Add($"Backhaul attached: {backhaul.Reference} is collected after the outbound delivery phase and returns south. Incremental live HGV distance: {bestIncrement:0.0} mi versus {standalone.Miles:0.0} mi as standalone work.");
        if (backhaul.Pallets <= 0)
            warnings.Add($"{backhaul.Reference}: return quantity is not stated, so it does not alter the outbound pallet-utilisation score.");

        runs[bestIndex] = selectedRun with
        {
            PlannedPallets = peakPallets,
            UtilisationPercent = selectedRun.CapacityPallets > 0 ? Math.Round((decimal)peakPallets / selectedRun.CapacityPallets * 100m, 1) : 0m,
            RoutingAvailable = true,
            Miles = bestCombined.Miles,
            DriveMinutes = bestCombined.DriveMinutes,
            Orders = selectedRun.Orders.Concat([backhaul]).ToList(),
            Stops = updatedStops,
            Warnings = warnings,
        };
        return true;
    }

    private async Task<(IReadOnlyList<BetaRoutePoint> Stops, BetaHgvRouteCost? Cost)> OptimiseWithinCollectionAndDeliveryPhasesAsync(
        IReadOnlyList<BetaRoutePoint> original,
        int collectionCount,
        CancellationToken ct)
    {
        var best = original.ToList();
        var bestCost = await routeProvider.GetRouteAsync(best, ct);
        if (bestCost is null) return (best, null);

        var checks = 0;
        foreach (var (start, endExclusive) in new[] { (0, collectionCount), (collectionCount, best.Count) })
        {
            var changed = true;
            while (changed && checks < MaxPhaseSwapChecks)
            {
                changed = false;
                for (var index = start; index + 1 < endExclusive && checks < MaxPhaseSwapChecks; index++)
                {
                    var candidate = best.ToList();
                    (candidate[index], candidate[index + 1]) = (candidate[index + 1], candidate[index]);
                    checks++;
                    var cost = await routeProvider.GetRouteAsync(candidate, ct);
                    if (cost is null || !Better(cost, bestCost)) continue;
                    best = candidate;
                    bestCost = cost;
                    changed = true;
                }
            }
        }

        return (best, bestCost);
    }

    internal static bool MeetsTimingWindow(DateOnly planningDate, IReadOnlyList<BetaDayOrderInput> orders, BetaHgvRouteCost route, BetaOptimiserOptions? optimiserOptions = null)
    {
        var options = (optimiserOptions ?? new BetaOptimiserOptions()).Validate();
        var start = orders.Select(order => order.CollectionTimeFrom).Where(value => value is not null)
            .Select(value => value!.Value).DefaultIfEmpty(new TimeOnly(0, 0)).Min();
        var deadline = orders.Select(order => order.DeliveryDeadline).Where(value => value is not null)
            .Select(value => value!.Value).DefaultIfEmpty(TimeOnly.MaxValue).Min();
        if (deadline == TimeOnly.MaxValue) return true;
        var finish = planningDate.ToDateTime(start).AddMinutes(PlannedRouteMinutes(route, orders.Count, options));
        var deadlineDate = deadline < start ? planningDate.AddDays(1) : planningDate;
        return finish <= deadlineDate.ToDateTime(deadline);
    }

    internal static int PlannedRouteMinutes(BetaHgvRouteCost route, int stopCount, BetaOptimiserOptions optimiserOptions)
    {
        var options = optimiserOptions.Validate();
        var bufferedDrive = (int)Math.Ceiling(route.DriveMinutes * (1m + options.TrafficBufferPercent / 100m));
        return bufferedDrive + Math.Max(0, stopCount) * options.AverageDwellMinutes;
    }

    internal static bool MeetsOperationalLimits(int stopCount, BetaHgvRouteCost route, BetaOptimiserOptions optimiserOptions)
    {
        var options = optimiserOptions.Validate();
        if (route.DriveMinutes > options.MaxDailyDrivingMinutes) return false;
        return PlannedRouteMinutes(route, stopCount, options) <= options.MaxDayLengthMinutes;
    }

    internal static List<BetaRoutePoint> BuildStops(IReadOnlyList<BetaDayOrderInput> orders)
    {
        var collections = DistinctPoints(orders
            .OrderBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(order => order.Collection));
        var deliveries = DistinctPoints(orders
            .OrderBy(order => order.CollectionTimeFrom ?? TimeOnly.MaxValue)
            .ThenBy(order => order.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(order => order.Delivery));
        return collections.Concat(deliveries).ToList();
    }

    private static List<BetaRoutePoint> AppendDistinct(IReadOnlyList<BetaRoutePoint> existing, params BetaRoutePoint[] append)
    {
        var result = existing.ToList();
        var seen = result.Select(PointKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var point in append)
            if (seen.Add(PointKey(point))) result.Add(point);
        return result;
    }

    private static List<BetaRoutePoint> DistinctPoints(IEnumerable<BetaRoutePoint> points)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BetaRoutePoint>();
        foreach (var point in points)
        {
            if (!seen.Add(PointKey(point))) continue;
            result.Add(point);
        }
        return result;
    }

    private static string PointKey(BetaRoutePoint point) => $"{point.Latitude:0.000000}|{point.Longitude:0.000000}|{NormalisePointName(point.Name)}";
    private static string NormalisePointName(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static IReadOnlyList<BetaDayOrderInput> ExpandOversizeOrders(IReadOnlyList<BetaDayOrderInput> orders)
    {
        var result = new List<BetaDayOrderInput>();
        foreach (var order in orders)
        {
            var capacity = Capacity(PalletFamily(order.PalletType));
            var remaining = Math.Max(order.Pallets, 0);
            while (remaining > capacity)
            {
                result.Add(order with { Pallets = capacity });
                remaining -= capacity;
            }
            if (remaining > 0) result.Add(order with { Pallets = remaining });
        }
        return result;
    }

    internal static bool IsSouthboundBackhaulCandidate(BetaDayOrderInput order, decimal minSouthboundLatitudeDelta) =>
        NormalisePeriod(order.Period) != "W3" &&
        order.RoutingMapped &&
        order.Collection.Latitude - order.Delivery.Latitude >= minSouthboundLatitudeDelta;

    private static bool Better(BetaHgvRouteCost candidate, BetaHgvRouteCost current) =>
        candidate.Miles < current.Miles - 0.1m ||
        (Math.Abs(candidate.Miles - current.Miles) <= 0.1m && candidate.DriveMinutes < current.DriveMinutes);

    private static int Capacity(string family) => family == "Euro" ? EuroCapacity : StandardCapacity;
    private static string PalletFamily(string? value) => value?.Contains("euro", StringComparison.OrdinalIgnoreCase) == true ? "Euro" : "Standard";
    private static string NormalisePeriod(string? value)
    {
        if (string.Equals(value, "W3", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Wave 3", StringComparison.OrdinalIgnoreCase)) return "W3";
        return string.Equals(value, "PM", StringComparison.OrdinalIgnoreCase) ? "PM" : "AM";
    }
    private static int PeriodRank(string? value) => NormalisePeriod(value) switch { "AM" => 0, "PM" => 1, "W3" => 2, _ => 3 };
}
