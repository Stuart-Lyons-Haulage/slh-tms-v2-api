using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Hubs;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record BackloadDispatchNotification(
    Guid LoadId,
    string VehicleReg,
    string DriverName,
    MatrixPoint CurrentPosition,
    IReadOnlyList<BackloadMatch> Matches);

public sealed record AcceptBackloadRequest(Guid LoadId, Guid OrderId);
public sealed record DeclineBackloadRequest(Guid LoadId, Guid OrderId, string Reason, string? Note = null);
public sealed record AcceptBackloadResult(Guid LoadId, Guid OrderId, string OrderReference, int CollectionSequence, int DeliverySequence);

public sealed class BackloadOperationsService(
    TmsDbContext db,
    IBackloadMatchingService matchingService,
    IHubContext<DispatchHub> dispatchHub,
    IOptions<HgvVehicleProfile> defaultProfile,
    ILogger<BackloadOperationsService> logger)
{
    private static readonly string[] CollectionKeys = ["collectionSite", "collectionLocation", "collection", "collectionAddress", "from"];
    private static readonly string[] DeliveryKeys = ["deliverySite", "deliveryLocation", "destination", "delivery", "deliveryAddress", "to"];

    public async Task<BackloadDispatchNotification?> EvaluateLoadAsync(Guid loadId, MatrixPoint currentPosition, CancellationToken ct)
    {
        var load = await LoadAsync(loadId, ct);
        if (load is null || load.Status is LoadStatus.Completed or LoadStatus.Cancelled || load.VehicleId is null) return null;

        var capacity = await ResolveRemainingPalletCapacityAsync(load, ct);
        if (capacity <= 0)
        {
            logger.LogDebug("Backload scan skipped for {LoadReference}: no remaining pallet capacity.", load.Reference);
            return null;
        }

        var candidates = await ReadCandidatesAsync(load.PlanningDate, ct);
        if (candidates.Count == 0) return null;

        var matches = await matchingService.FindMatchesAsync(currentPosition, capacity, candidates, defaultProfile.Value, ct);
        if (matches.Count == 0) return null;

        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.VehicleId.Value, ct);
        var driver = load.DriverId is Guid driverId
            ? await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == driverId, ct)
            : null;
        var notification = new BackloadDispatchNotification(
            load.Id,
            vehicle?.Registration ?? "Unassigned vehicle",
            driver?.DisplayName ?? "Unassigned driver",
            currentPosition,
            matches);

        await dispatchHub.Clients.All.SendAsync("BackloadMatches", notification, ct);
        return notification;
    }

    public async Task<AcceptBackloadResult> AcceptAsync(AcceptBackloadRequest request, string actor, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var load = await LoadAsync(request.LoadId, ct)
            ?? throw new KeyNotFoundException($"Run {request.LoadId} was not found.");
        if (load.Status is LoadStatus.Completed or LoadStatus.Cancelled)
            throw new InvalidOperationException($"Run {load.Reference} can no longer accept a backload.");
        if (load.VehicleId is null || load.DriverId is null)
            throw new InvalidOperationException($"Run {load.Reference} must have both a vehicle and driver before a backload can be accepted.");

        var order = await db.TransportOrders.SingleOrDefaultAsync(item => item.Id == request.OrderId, ct)
            ?? throw new KeyNotFoundException($"Order {request.OrderId} was not found.");
        if (order.Status != OrderStatus.ReadyToPlan)
            throw new InvalidOperationException($"Order {order.Reference} is no longer unallocated.");
        if (order.CollectionDate != load.PlanningDate)
            throw new InvalidOperationException($"Order {order.Reference} is for {order.CollectionDate:yyyy-MM-dd}, not run date {load.PlanningDate:yyyy-MM-dd}.");

        var candidate = (await ReadCandidatesAsync(load.PlanningDate, ct)).SingleOrDefault(item => item.OrderId == order.Id)
            ?? throw new InvalidOperationException($"Order {order.Reference} no longer has complete Site Master coordinates for backload allocation.");

        var usedBefore = load.PalletSpacesUsed ?? await ResolveUsedPalletsAsync(load, ct);
        var remaining = await ResolveRemainingPalletCapacityAsync(load, ct);
        if (candidate.PalletCount > remaining)
            throw new InvalidOperationException($"Order {order.Reference} requires {candidate.PalletCount} pallet spaces but run {load.Reference} has {remaining} remaining.");

        var lastSequence = load.Stops.Count == 0 ? 0 : load.Stops.Max(stop => stop.Sequence);
        var collectionSequence = lastSequence + 1;
        var deliverySequence = lastSequence + 2;
        load.Stops.Add(new LoadStop
        {
            LoadId = load.Id,
            OrderId = order.Id,
            Sequence = collectionSequence,
            Name = candidate.CollectionPointName,
            Latitude = candidate.CollectionPoint.Latitude,
            Longitude = candidate.CollectionPoint.Longitude,
            PlannerNote = $"Accepted backload · {order.Reference}"
        });
        load.Stops.Add(new LoadStop
        {
            LoadId = load.Id,
            OrderId = order.Id,
            Sequence = deliverySequence,
            Name = candidate.DeliveryPointName,
            Latitude = candidate.DeliveryPoint.Latitude,
            Longitude = candidate.DeliveryPoint.Longitude,
            PlannerNote = $"Accepted backload · {order.Reference}"
        });
        load.PalletSpacesUsed = usedBefore + candidate.PalletCount;
        order.Status = OrderStatus.Planned;

        if (db.Entry(load).State != EntityState.Detached)
            await db.SaveChangesAsync(ct);
        await PlanningRegisterStore.SaveLoadAsync(db, load, actor, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("Backload {OrderReference} accepted onto {LoadReference} by {Actor}.", order.Reference, load.Reference, actor);
        return new AcceptBackloadResult(load.Id, order.Id, order.Reference, collectionSequence, deliverySequence);
    }

    public async Task RecordDeclineAsync(DeclineBackloadRequest request, string actor, CancellationToken ct)
    {
        var allowedReasons = new HashSet<string>(["capacity", "timing", "customer instruction", "other"], StringComparer.OrdinalIgnoreCase);
        var reason = request.Reason.Trim();
        if (!allowedReasons.Contains(reason))
            throw new ArgumentException("Decline reason must be capacity, timing, customer instruction, or other.", nameof(request));

        var order = await db.TransportOrders.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.OrderId, ct)
            ?? throw new KeyNotFoundException($"Order {request.OrderId} was not found.");
        var load = await LoadAsync(request.LoadId, ct)
            ?? throw new KeyNotFoundException($"Run {request.LoadId} was not found.");
        var now = DateTimeOffset.UtcNow;
        var row = new StagedImport
        {
            EntityType = "backload-decline",
            IdempotencyKey = $"backload-decline:{load.Id:N}:{order.Id:N}:{now:yyyyMMddHHmmssfff}",
            PayloadJson = JsonSerializer.Serialize(new
            {
                loadId = load.Id,
                loadReference = load.Reference,
                orderId = order.Id,
                orderReference = order.Reference,
                reason,
                note = request.Note,
                actor,
                declinedAtUtc = now
            }),
            Status = StagingStatus.Promoted,
            Source = "Dispatch backload decision",
            ReviewedAtUtc = now,
            ReviewedBy = actor,
            ReviewNote = reason
        };
        db.StagedImports.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<BackloadCandidate>> ReadCandidatesAsync(DateOnly planningDate, CancellationToken ct)
    {
        var orders = await db.TransportOrders.AsNoTracking()
            .Where(order => order.CollectionDate == planningDate && order.Status == OrderStatus.ReadyToPlan)
            .OrderBy(order => order.Reference)
            .ToListAsync(ct);
        if (orders.Count == 0) return [];

        var movementIds = orders.Where(order => order.SourceMovementId != null).Select(order => order.SourceMovementId!.Value).Distinct().ToList();
        var movements = movementIds.Count == 0
            ? []
            : await db.OrderMovements.AsNoTracking().Where(movement => movementIds.Contains(movement.Id) && movement.CurrentRevisionId != null).ToListAsync(ct);
        var revisionIds = movements.Select(movement => movement.CurrentRevisionId!.Value).Distinct().ToList();
        var sourceLines = revisionIds.Count == 0
            ? []
            : await db.OrderSourceLines.AsNoTracking()
                .Where(line => revisionIds.Contains(line.RevisionId) && (line.CollectionDate == planningDate || line.CollectionDate == null))
                .OrderBy(line => line.CollectionTimeFrom)
                .ToListAsync(ct);
        var revisionByMovement = movements.ToDictionary(movement => movement.Id, movement => movement.CurrentRevisionId!.Value);
        var linesByRevision = sourceLines.GroupBy(line => line.RevisionId).ToDictionary(group => group.Key, group => group.ToList());

        var stagedIds = orders.Where(order => order.SourceStagedImportId != null).Select(order => order.SourceStagedImportId!.Value).Distinct().ToList();
        var staged = stagedIds.Count == 0
            ? new Dictionary<Guid, StagedImport>()
            : await db.StagedImports.AsNoTracking().Where(item => stagedIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, ct);

        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).Take(5000).ToListAsync(ct);
        try { await MasterDetailStore.EnrichSitesAsync(db, sites, ct); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Backload matching could not enrich Site Master coordinates.");
        }

        var result = new List<BackloadCandidate>();
        foreach (var order in orders)
        {
            OrderSourceLine? line = null;
            if (order.SourceMovementId is Guid movementId && revisionByMovement.TryGetValue(movementId, out var revisionId) && linesByRevision.TryGetValue(revisionId, out var lines))
                line = lines.FirstOrDefault();

            staged.TryGetValue(order.SourceStagedImportId ?? Guid.Empty, out var source);
            var collectionName = First(line?.CollectionSite, JsonString(source?.PayloadJson, CollectionKeys), order.SellerName);
            var deliveryName = First(order.MarketName, line?.DeliverySite, JsonString(source?.PayloadJson, DeliveryKeys));
            var collectionSite = MatchSite(sites, collectionName);
            var deliverySite = MatchSite(sites, deliveryName);
            if (collectionSite?.Latitude is null || collectionSite.Longitude is null || deliverySite?.Latitude is null || deliverySite.Longitude is null)
                continue;

            result.Add(new BackloadCandidate(
                order.Id,
                order.Reference,
                collectionSite.DriverTextName ?? collectionSite.Name,
                new MatrixPoint(collectionSite.Latitude.Value, collectionSite.Longitude.Value),
                deliverySite.DriverTextName ?? deliverySite.Name,
                new MatrixPoint(deliverySite.Latitude.Value, deliverySite.Longitude.Value),
                Math.Max(0, order.Pallets ?? line?.Pallets ?? 0)));
        }
        return result;
    }

    private async Task<Load?> LoadAsync(Guid loadId, CancellationToken ct)
    {
        var load = await db.Loads.Include(item => item.Stops).SingleOrDefaultAsync(item => item.Id == loadId, ct);
        if (load is not null) return load;
        db.ChangeTracker.Clear();
        return await PlanningRegisterStore.GetLoadAsync(db, loadId, ct);
    }

    private async Task<int> ResolveRemainingPalletCapacityAsync(Load load, CancellationToken ct)
    {
        var total = load.TotalPalletSpaces;
        if (total is null && load.TrailerId is Guid trailerId)
        {
            var trailer = await db.Trailers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == trailerId, ct);
            total = trailer?.StandardCapacity ?? trailer?.EuroCapacity;
        }
        if (total is null || total <= 0) return 0;
        var used = load.PalletSpacesUsed ?? await ResolveUsedPalletsAsync(load, ct);
        return Math.Max(0, (int)Math.Floor(total.Value - used));
    }

    private async Task<decimal> ResolveUsedPalletsAsync(Load load, CancellationToken ct)
    {
        var orderIds = load.Stops.Where(stop => stop.OrderId != null).Select(stop => stop.OrderId!.Value).Distinct().ToList();
        if (orderIds.Count == 0) return 0;
        return await db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)).SumAsync(order => (decimal?)(order.Pallets ?? 0), ct) ?? 0m;
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
}
