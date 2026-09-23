using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record BetaPlannerStopRequest(
    string Name,
    string? OrderKey = null,
    string? Reference = null,
    int? Pallets = null,
    string? Role = null);
public sealed record BetaPlannerRouteRequest(string Reference, IReadOnlyList<BetaPlannerStopRequest> Stops);
public sealed record BetaPlannerComparisonRequest(DateOnly PlanningDate, IReadOnlyList<BetaPlannerRouteRequest> Routes);

public sealed record BetaDayPlanOrderResult(
    Guid OrderId,
    Guid SourceLineId,
    string Reference,
    string CustomerCode,
    string Period,
    string? PalletType,
    int Pallets,
    string CollectionName,
    string DeliveryName);

public sealed record BetaDayPlanRunResult(
    string Reference,
    string Period,
    string PalletFamily,
    int CapacityPallets,
    int PlannedPallets,
    decimal UtilisationPercent,
    bool RoutingAvailable,
    decimal? Miles,
    int? DriveMinutes,
    IReadOnlyList<BetaDayPlanOrderResult> Orders,
    IReadOnlyList<object> Stops,
    IReadOnlyList<string> Warnings);

public sealed record BetaDayPlanResult(
    DateOnly PlanningDate,
    DateTimeOffset GeneratedAtUtc,
    string RoutingPolicy,
    int EligibleOrderLines,
    int PlannedOrderLines,
    int UnmappedOrderLines,
    int RunCount,
    int RoutedRunCount,
    int TotalPallets,
    decimal? TotalMiles,
    int? TotalDriveMinutes,
    bool RoutingComplete,
    IReadOnlyList<BetaDayPlanRunResult> Runs,
    IReadOnlyList<string> Warnings);

public sealed record BetaPlannerRouteResult(
    string Reference,
    int StopCount,
    int OrderLineCount,
    int PlannedPallets,
    bool RoutingAvailable,
    decimal? Miles,
    int? DriveMinutes,
    IReadOnlyList<string> Stops,
    IReadOnlyList<string> Warnings);

public sealed record BetaPlannerPlanResult(
    DateOnly PlanningDate,
    DateTimeOffset AnalysedAtUtc,
    int RouteCount,
    int RoutedRouteCount,
    int OrderLineCount,
    int TotalPallets,
    decimal? TotalMiles,
    int? TotalDriveMinutes,
    bool RoutingComplete,
    IReadOnlyList<BetaPlannerRouteResult> Routes,
    IReadOnlyList<string> Warnings);

public sealed record BetaDayPlanComparisonResult(
    DateOnly PlanningDate,
    BetaDayPlanResult Beta,
    BetaPlannerPlanResult Lyons,
    BetaDayPlanReconciliation Reconciliation);

public sealed class BetaDayPlanService(
    TmsDbContext db,
    BetaDayPlanBuilder builder,
    IBetaHgvRouteProvider routeProvider,
    ILogger<BetaDayPlanService> logger,
    SiteTimingRuleStore timingRuleStore)
{
    private static readonly string[] ReferenceKeys = ["reference", "orderReference", "po", "poNumber", "purchaseOrder", "loadReference"];
    private static readonly string[] CollectionKeys = ["collectionSite", "collectionLocation", "collection", "collectionAddress", "from"];
    private static readonly string[] DeliveryKeys = ["deliverySite", "deliveryLocation", "destination", "delivery", "deliveryAddress", "to"];
    private static readonly string[] PalletTypeKeys = ["palletType", "pallet_type", "handlingUnitType", "unitType", "handlingUnit"];
    private static readonly string[] CollectionTimeKeys = ["collectionTimeFrom", "collectionTime", "collectFrom", "collectionFrom"];

    public async Task<BetaDayPlanResult> BuildDayAsync(DateOnly planningDate, CancellationToken ct)
    {
        var inputs = await ReadOrderInputsAsync(planningDate, ct);
        var runs = await builder.BuildAsync(planningDate, inputs, ct);
        var resultRuns = runs.Select(run => new BetaDayPlanRunResult(
            run.Reference,
            run.Period,
            run.PalletFamily,
            run.CapacityPallets,
            run.PlannedPallets,
            run.UtilisationPercent,
            run.RoutingAvailable,
            run.Miles,
            run.DriveMinutes,
            run.Orders.Select(order => new BetaDayPlanOrderResult(
                order.OrderId, order.SourceLineId, order.Reference, order.CustomerCode, order.Period,
                order.PalletType, order.Pallets, order.Collection.Name, order.Delivery.Name)).ToList(),
            run.Stops.Select((stop, index) => (object)new { sequence = index + 1, stop.Name }).ToList(),
            run.Warnings)).ToList();

        var routingComplete = resultRuns.Count > 0 && resultRuns.All(run => run.RoutingAvailable);
        var warnings = new List<string>();
        var unmapped = inputs.Count(order => !order.RoutingMapped);
        var unquantified = inputs.Count(order => order.Pallets <= 0);
        if (unmapped > 0)
            warnings.Add($"{unmapped} order line(s) are included in the day but need Site Master coordinates before a complete HGV mileage comparison is possible.");
        if (unquantified > 0)
            warnings.Add($"{unquantified} movement(s) have no numeric pallet quantity. They remain in routing and reconciliation but are excluded from pallet-capacity utilisation.");
        if (!routingComplete && resultRuns.Count > 0)
            warnings.Add("Whole-day mileage is partial because at least one run has no live Azure Maps HGV route. No Haversine/crow-fly replacement is used.");
        if (inputs.Count == 0)
            warnings.Add("No PlannerReady/current order lines were found for this collection date.");

        var plannedSourceLines = resultRuns
            .SelectMany(run => run.Orders)
            .Select(order => order.SourceLineId)
            .Distinct()
            .Count();

        return new BetaDayPlanResult(
            planningDate,
            DateTimeOffset.UtcNow,
            "Live Azure Maps fastest commercial HGV route with traffic, plus configured dwell and traffic-buffer assumptions. Approximate/Haversine evidence is excluded from optimisation decisions.",
            inputs.Select(input => input.SourceLineId).Distinct().Count(),
            plannedSourceLines,
            inputs.Where(input => !input.RoutingMapped).Select(input => input.SourceLineId).Distinct().Count(),
            resultRuns.Count,
            resultRuns.Count(run => run.RoutingAvailable),
            resultRuns.Sum(run => run.PlannedPallets),
            resultRuns.Where(run => run.RoutingAvailable).Sum(run => run.Miles ?? 0m),
            resultRuns.Where(run => run.RoutingAvailable).Sum(run => run.DriveMinutes ?? 0),
            routingComplete,
            resultRuns,
            warnings);
    }

    public async Task<BetaDayPlanComparisonResult> CompareAsync(BetaPlannerComparisonRequest request, CancellationToken ct)
    {
        var beta = await BuildDayAsync(request.PlanningDate, ct);
        var lyons = await RouteLyonsPlanAsync(request, ct);
        var betaLines = beta.Runs.SelectMany(run => run.Orders)
            .Select(order => new BetaComparisonOrderLine(order.Reference, order.CollectionName, order.DeliveryName, order.Pallets))
            .ToList();
        var lyonsLines = ExtractLyonsLines(request);
        var reconciliation = BetaDayPlanReconciler.Reconcile(
            betaLines,
            lyonsLines,
            beta.RunCount,
            lyons.RouteCount,
            beta.RoutingComplete,
            lyons.RoutingComplete,
            beta.TotalMiles,
            lyons.TotalMiles,
            beta.TotalDriveMinutes,
            lyons.TotalDriveMinutes);
        return new BetaDayPlanComparisonResult(request.PlanningDate, beta, lyons, reconciliation);
    }

    private async Task<BetaPlannerPlanResult> RouteLyonsPlanAsync(BetaPlannerComparisonRequest request, CancellationToken ct)
    {
        var sites = await SitesAsync(ct);
        var routes = new List<BetaPlannerRouteResult>();
        var warnings = new List<string>();
        foreach (var route in request.Routes)
        {
            var routeWarnings = new List<string>();
            var points = new List<BetaRoutePoint>();
            foreach (var stop in route.Stops)
            {
                var site = MatchSite(sites, stop.Name);
                if (site?.Latitude is null || site.Longitude is null)
                {
                    routeWarnings.Add($"{stop.Name}: no Site Master coordinates; this Lyons run cannot be included in the complete HGV mileage comparison.");
                    continue;
                }
                points.Add(new BetaRoutePoint(site.DriverTextName ?? site.Name, site.Latitude.Value, site.Longitude.Value));
            }

            BetaHgvRouteCost? cost = null;
            if (points.Count == route.Stops.Count && points.Count >= 2)
                cost = await routeProvider.GetRouteAsync(points, ct);
            if (cost is null && route.Stops.Count >= 2 && routeWarnings.Count == 0)
                routeWarnings.Add("Live Azure Maps HGV routing was unavailable for this Lyons run. No approximate mileage was substituted.");

            var orderLines = ExtractLyonsLines(new BetaPlannerComparisonRequest(request.PlanningDate, [route]));
            routes.Add(new BetaPlannerRouteResult(
                route.Reference,
                route.Stops.Count,
                orderLines.Count,
                orderLines.Sum(line => line.Pallets),
                cost is not null,
                cost?.Miles,
                cost?.DriveMinutes,
                route.Stops.Select(stop => stop.Name).ToList(),
                routeWarnings));
        }

        if (routes.Any(route => !route.RoutingAvailable))
            warnings.Add("The uploaded Lyons plan has one or more unrouted runs; the whole-day mileage delta will remain unavailable until all sites have live Azure HGV evidence.");
        var routingComplete = routes.Count > 0 && routes.All(route => route.RoutingAvailable);
        var lines = ExtractLyonsLines(request);
        return new BetaPlannerPlanResult(
            request.PlanningDate,
            DateTimeOffset.UtcNow,
            routes.Count,
            routes.Count(route => route.RoutingAvailable),
            lines.Count,
            lines.Sum(line => line.Pallets),
            routes.Where(route => route.RoutingAvailable).Sum(route => route.Miles ?? 0m),
            routes.Where(route => route.RoutingAvailable).Sum(route => route.DriveMinutes ?? 0),
            routingComplete,
            routes,
            warnings);
    }

    private async Task<List<BetaDayOrderInput>> ReadOrderInputsAsync(DateOnly planningDate, CancellationToken ct)
    {
        var movements = await db.OrderMovements.AsNoTracking()
            .Where(item => item.LifecycleStatus == OrderMovementStatus.PlannerReady && item.CurrentRevisionId != null)
            .OrderBy(item => item.CustomerCode).ThenBy(item => item.StableMovementKey)
            .ToListAsync(ct);
        var revisionIds = movements.Select(item => item.CurrentRevisionId!.Value).ToList();
        var revisions = revisionIds.Count == 0
            ? []
            : await db.OrderRevisions.AsNoTracking().Where(item => revisionIds.Contains(item.Id)).ToListAsync(ct);
        var lines = revisionIds.Count == 0
            ? []
            : await db.OrderSourceLines.AsNoTracking()
                .Where(line => revisionIds.Contains(line.RevisionId) &&
                    (line.CollectionDate == planningDate || (line.CollectionDate == null && line.DeliveryDate == planningDate)))
                .OrderBy(line => line.CollectionTimeFrom).ThenBy(line => line.SourceRowKey)
                .ToListAsync(ct);

        var movementById = movements.ToDictionary(item => item.Id);
        var revisionById = revisions.ToDictionary(item => item.Id);
        var liveOrders = await db.TransportOrders.AsNoTracking()
            .Where(order => order.Status != OrderStatus.Cancelled && order.CollectionDate == planningDate)
            .OrderBy(order => order.Reference)
            .ToListAsync(ct);
        var liveByMovement = liveOrders.Where(order => order.SourceMovementId is not null)
            .GroupBy(order => order.SourceMovementId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(order => order.CreatedAtUtc).First());
        var sites = await SitesAsync(ct);
        var timingRules = await timingRuleStore.ReadAsync(ct);
        var inputs = new List<BetaDayOrderInput>();
        var representedMovementIds = new HashSet<Guid>();

        foreach (var line in lines)
        {
            if (!revisionById.TryGetValue(line.RevisionId, out var revision) || !movementById.TryGetValue(revision.MovementId, out var movement)) continue;
            representedMovementIds.Add(movement.Id);
            liveByMovement.TryGetValue(movement.Id, out var live);
            var collectionName = First(line.CollectionSite, live?.SellerName);
            var deliveryName = First(live?.MarketName, line.DeliverySite);
            var reference = First(live?.Reference, JsonString(line.PayloadJson, ReferenceKeys), line.LoadReference, line.SourceRowKey) ?? line.SourceRowKey;
            inputs.Add(ApplyTiming(ToInput(
                live?.Id ?? line.Id,
                line.Id,
                reference,
                First(live?.CustomerCode, movement.CustomerCode) ?? movement.CustomerCode,
                line.PalletType,
                line.Pallets ?? 0,
                line.CollectionTimeFrom,
                collectionName,
                deliveryName,
                sites), line.CollectionDate ?? planningDate, timingRules, sites));
        }

        var fallbackOrders = liveOrders
            .Where(order => order.SourceMovementId is null || !representedMovementIds.Contains(order.SourceMovementId.Value))
            .ToList();
        var stagedIds = fallbackOrders.Where(order => order.SourceStagedImportId is not null).Select(order => order.SourceStagedImportId!.Value).Distinct().ToList();
        var staged = stagedIds.Count == 0
            ? new Dictionary<Guid, StagedImport>()
            : await db.StagedImports.AsNoTracking().Where(item => stagedIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, ct);
        foreach (var order in fallbackOrders)
        {
            staged.TryGetValue(order.SourceStagedImportId ?? Guid.Empty, out var source);
            var payload = source?.PayloadJson;
            var collectionName = First(JsonString(payload, CollectionKeys), order.SellerName);
            var deliveryName = First(order.MarketName, JsonString(payload, DeliveryKeys));
            var collectionTime = ParseTime(JsonString(payload, CollectionTimeKeys));
            inputs.Add(ApplyTiming(ToInput(
                order.Id,
                order.Id,
                order.Reference,
                order.CustomerCode,
                JsonString(payload, PalletTypeKeys),
                order.Pallets ?? 0,
                collectionTime,
                collectionName,
                deliveryName,
                sites), order.CollectionDate, timingRules, sites));
        }

        return inputs;
    }

    private static BetaDayOrderInput ToInput(
        Guid orderId,
        Guid sourceLineId,
        string reference,
        string customerCode,
        string? palletType,
        int pallets,
        TimeOnly? collectionTime,
        string? collectionName,
        string? deliveryName,
        IReadOnlyList<Site> sites)
    {
        var collectionSite = MatchSite(sites, collectionName);
        var deliverySite = MatchSite(sites, deliveryName);
        var collectionMapped = collectionSite?.Latitude is not null && collectionSite.Longitude is not null;
        var deliveryMapped = deliverySite?.Latitude is not null && deliverySite.Longitude is not null;
        var mapped = collectionMapped && deliveryMapped;
        var issues = new List<string>();
        if (!collectionMapped) issues.Add($"collection '{collectionName ?? "missing"}'");
        if (!deliveryMapped) issues.Add($"delivery '{deliveryName ?? "missing"}'");
        var warning = issues.Count == 0 ? null : $"{reference}: Site Master routing is missing for {string.Join(" and ", issues)}.";
        var collection = collectionMapped
            ? new BetaRoutePoint(collectionSite!.DriverTextName ?? collectionSite.Name, collectionSite.Latitude!.Value, collectionSite.Longitude!.Value)
            : new BetaRoutePoint(collectionName ?? "Collection unmapped", 0m, 0m);
        var delivery = deliveryMapped
            ? new BetaRoutePoint(deliverySite!.DriverTextName ?? deliverySite.Name, deliverySite.Latitude!.Value, deliverySite.Longitude!.Value)
            : new BetaRoutePoint(deliveryName ?? "Delivery unmapped", 0m, 0m);
        var period = IsWave3(customerCode, palletType, collectionName, deliveryName, collectionTime)
            ? "W3"
            : collectionTime is { } time && time >= new TimeOnly(17, 0) ? "PM" : "AM";
        return new BetaDayOrderInput(
            orderId,
            sourceLineId,
            reference,
            customerCode,
            period,
            palletType,
            pallets,
            collectionTime,
            collection,
            delivery,
            mapped,
            warning);
    }

    private static BetaDayOrderInput ApplyTiming(BetaDayOrderInput input, DateOnly date, IReadOnlyList<SiteTimingRule> rules, IReadOnlyList<Site> sites)
    {
        var rule = rules.FirstOrDefault(candidate => SiteTimingRuleMatcher.Match(candidate, input.Collection.Name, input.Delivery.Name, input.PalletType, sites));
        if (rule is null) return input;
        var collection = SiteTimingRuleMatcher.CollectionWindow(rule, date);
        var deadline = SiteTimingRuleMatcher.DeliveryWindow(rule, date).End;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        static TimeOnly? Local(DateTimeOffset? value, TimeZoneInfo zone) => value is null ? null : TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.Value, zone).DateTime);
        return input with
        {
            CollectionTimeFrom = Local(collection.Start, zone) ?? input.CollectionTimeFrom,
            CollectionTimeTo = Local(collection.End, zone),
            DeliveryDeadline = Local(deadline, zone),
            TimingRule = rule.RouteCombination
        };
    }

    private static bool IsWave3(string? customerCode, string? unit, string? collection, string? delivery, TimeOnly? collectionTime)
    {
        var text = string.Join(" ", new[] { customerCode, unit, collection, delivery }.Where(value => !string.IsNullOrWhiteSpace(value))).ToUpperInvariant();
        if (text.Contains("MARKET") || text.Contains("WAVE 3") || text.Contains("WAVE3")) return true;
        return text.Contains("WAITROSE") && collectionTime is { } time && time >= new TimeOnly(17, 0);
    }

    private async Task<List<Site>> SitesAsync(CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).Take(5000).ToListAsync(ct);
        try { await MasterDetailStore.EnrichSitesAsync(db, sites, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Beta Optimiser could not enrich Site Master details; unmapped work will remain visible.");
        }
        return sites;
    }

    internal static List<BetaComparisonOrderLine> ExtractLyonsLines(BetaPlannerComparisonRequest request)
    {
        var result = new List<BetaComparisonOrderLine>();
        foreach (var route in request.Routes)
        {
            var groups = route.Stops
                .Select((stop, index) => new { Stop = stop, Index = index })
                .GroupBy(item => string.IsNullOrWhiteSpace(item.Stop.OrderKey) ? $"{route.Reference}:{item.Index}" : $"{route.Reference}:{item.Stop.OrderKey}");
            foreach (var group in groups)
            {
                var collection = group.FirstOrDefault(item => string.Equals(item.Stop.Role, "Collection", StringComparison.OrdinalIgnoreCase))?.Stop
                    ?? group.OrderBy(item => item.Index).First().Stop;
                var delivery = group.FirstOrDefault(item => string.Equals(item.Stop.Role, "Delivery", StringComparison.OrdinalIgnoreCase))?.Stop
                    ?? group.OrderByDescending(item => item.Index).First().Stop;
                if (ReferenceEquals(collection, delivery) && group.Count() == 1) continue;
                var reference = group.Select(item => item.Stop.Reference).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                var pallets = group.Select(item => item.Stop.Pallets).FirstOrDefault(value => value is > 0) ?? 0;
                result.Add(new BetaComparisonOrderLine(reference, collection.Name, delivery.Name, pallets));
            }
        }
        return result;
    }

    private static Site? MatchSite(IReadOnlyList<Site> sites, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var key = Normalise(raw);
        var exact = sites.FirstOrDefault(site => CandidateNames(site).Any(value => Normalise(value) == key));
        if (exact is not null) return exact;
        return sites.FirstOrDefault(site => CandidateNames(site).Any(value =>
        {
            var candidate = Normalise(value);
            return candidate.Length >= 5 && (key.Contains(candidate, StringComparison.OrdinalIgnoreCase) || candidate.Contains(key, StringComparison.OrdinalIgnoreCase));
        }));
    }

    private static IEnumerable<string> CandidateNames(Site site)
    {
        yield return site.Name;
        if (!string.IsNullOrWhiteSpace(site.DriverTextName)) yield return site.DriverTextName;
        if (!string.IsNullOrWhiteSpace(site.ExternalCode)) yield return site.ExternalCode;
        if (!string.IsNullOrWhiteSpace(site.Aliases))
            foreach (var alias in site.Aliases.Split(new[] { ',', ';', '|', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return alias;
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string? First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? JsonString(string? json, IReadOnlyList<string> keys)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return FindString(document.RootElement, keys);
        }
        catch (JsonException) { return null; }
    }

    private static string? FindString(JsonElement element, IReadOnlyList<string> keys)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                if (keys.Any(key => string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)) && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    return property.Value.ToString();
            foreach (var property in element.EnumerateObject())
            {
                var nested = FindString(property.Value, keys);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, keys);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static TimeOnly? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (TimeOnly.TryParse(value, out var time)) return time;
        if (DateTimeOffset.TryParse(value, out var dateTime)) return TimeOnly.FromDateTime(dateTime.DateTime);
        return null;
    }
}
