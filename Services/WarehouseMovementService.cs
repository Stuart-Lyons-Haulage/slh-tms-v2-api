using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class WarehouseMovementService(TmsDbContext db)
{
    private const string CanonicalSiteName = "SLH-Lyons Consolidation Centre FRV";

    public async Task<WarehouseDailyResult> BuildDailyAsync(DateOnly date, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var canonical = sites.FirstOrDefault(x => Normalize(x.Name) == Normalize(CanonicalSiteName) || Normalize(x.ExternalCode) == "slhfrv");
        if (canonical is null) return new(date, [], [], new(0, 0, 0, 0));
        var aliasMappings = await db.IntegrationMappings.AsNoTracking()
            .Where(x => x.Active && x.TmsEntityType == "Site" && x.TmsEntityId == canonical.Id && x.MappingKind == "SiteAlias")
            .ToListAsync(ct);
        var warehouseNames = WarehouseNames(canonical, aliasMappings);

        // Same sources as Pallet Order and Planner: SQL orders/runs merged with the planning
        // register. Yesterday is included so overnight work arriving today shows as inbound.
        var orders = await ReadOrdersAsync(date.AddDays(-1), date, ct);
        var loads = (await PlanningResilience.ReadLoadsAsync(db, date, ct))
            .Concat(await PlanningResilience.ReadLoadsAsync(db, date.AddDays(-1), ct))
            .Where(x => x.Status != LoadStatus.Cancelled)
            .GroupBy(x => x.Id).Select(x => x.First()).ToList();
        var loadById = loads.ToDictionary(x => x.Id);
        var allocations = await ReadLatestAllocations(loads.Select(x => x.Id).ToList(), [date, date.AddDays(-1)], ct);
        var vehicleIds = loads.Where(x => x.VehicleId is not null).Select(x => x.VehicleId!.Value).Distinct().ToList();
        var trailerIds = loads.Where(x => x.TrailerId is not null).Select(x => x.TrailerId!.Value).Distinct().ToList();
        var driverIds = loads.Where(x => x.DriverId is not null).Select(x => x.DriverId!.Value).Distinct().ToList();
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => vehicleIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var trailers = await db.Trailers.AsNoTracking().Where(x => trailerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var drivers = await db.Drivers.AsNoTracking().Where(x => driverIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        var inbound = new List<WarehouseMovementRow>();
        var outbound = new List<WarehouseMovementRow>();

        // Orders on runs. Source lines (when the order came through the new intake) give the
        // exact sites, pallet type and load reference; otherwise the order itself is used.
        var orderById = orders.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        var missingIds = allocations.Select(x => x.OrderId).Where(id => !orderById.ContainsKey(id)).Distinct().ToList();
        if (missingIds.Count > 0)
            foreach (var order in await db.TransportOrders.AsNoTracking().Where(x => missingIds.Contains(x.Id)).ToListAsync(ct))
                orderById[order.Id] = order;
        var lineIds = allocations.Where(x => x.SourceLineId is not null).Select(x => x.SourceLineId!.Value).Distinct().ToList();
        var lines = lineIds.Count == 0 ? [] : await db.OrderSourceLines.AsNoTracking().Where(x => lineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var onRun = new HashSet<Guid>();
        foreach (var allocation in allocations.Where(x => x.Pallets > 0))
        {
            if (!loadById.TryGetValue(allocation.LoadId, out var load)) continue;
            orderById.TryGetValue(allocation.OrderId, out var order);
            OrderSourceLine? line = allocation.SourceLineId is Guid lineId && lines.TryGetValue(lineId, out var found) ? found : null;
            if (order is null && line is null) continue;
            if (order?.Status == OrderStatus.Cancelled) continue;
            var from = line?.CollectionSite ?? order?.SellerName;
            var to = line?.DeliverySite ?? order?.StallNumber;
            var direction = MatchesWarehouse(to, warehouseNames) ? "Inbound" : MatchesWarehouse(from, warehouseNames) ? "Outbound" : null;
            if (direction is null) continue;
            var due = direction == "Inbound"
                ? line?.DeliveryDate ?? order?.DeliveryDate ?? order?.CollectionDate
                : line?.CollectionDate ?? order?.CollectionDate;
            // Yesterday's runs only count when they arrive here today.
            if (load.PlanningDate != date && (direction != "Inbound" || due != date)) continue;
            onRun.Add(allocation.OrderId);
            var stop = load.Stops.OrderBy(x => x.Sequence).FirstOrDefault(x => MatchesWarehouse(x.Name, warehouseNames));
            var time = line?.CollectionTimeFrom;
            (direction == "Inbound" ? inbound : outbound).Add(new WarehouseMovementRow(direction, load.Id, load.Reference, Period(time),
                load.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var driver) ? driver.DisplayName : null,
                load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var vehicle) ? vehicle.Registration : null,
                load.TrailerId is Guid trailerId && trailers.TryGetValue(trailerId, out var trailer) ? trailer.TrailerNumber : null,
                order?.CustomerCode ?? "Unknown", from, to, order?.Reference,
                line?.LoadReference, line?.PalletType, allocation.Pallets, line?.TemperatureRequirement,
                due, direction == "Inbound" ? null : time, stop?.PlannedArrivalUtc, load.Status.ToString(), null)
            {
                MovementKey = line is not null
                    ? $"load:{load.Id:N}:line:{line.Id:N}:{direction.ToLowerInvariant()}"
                    : $"load:{load.Id:N}:order:{allocation.OrderId:N}:{direction.ToLowerInvariant()}",
                Source = "Planned run"
            });
        }

        // Approved work coming to or leaving SLH that isn't on a run yet, so the warehouse sees it coming.
        foreach (var order in orders.Where(x => x.Status != OrderStatus.Cancelled && !onRun.Contains(x.Id) && !allocations.Any(a => a.OrderId == x.Id && a.Pallets > 0 && a.Date == date)))
        {
            string? direction = null;
            if (MatchesWarehouse(order.StallNumber, warehouseNames) && (order.DeliveryDate ?? order.CollectionDate) == date) direction = "Inbound";
            else if (MatchesWarehouse(order.SellerName, warehouseNames) && order.CollectionDate == date) direction = "Outbound";
            if (direction is null) continue;
            (direction == "Inbound" ? inbound : outbound).Add(new WarehouseMovementRow(direction, Guid.Empty, "Not on a run yet", "Unallocated", null, null, null,
                order.CustomerCode, order.SellerName, order.StallNumber, order.Reference, null, null, order.Pallets ?? 0, null,
                direction == "Inbound" ? order.DeliveryDate ?? order.CollectionDate : order.CollectionDate, null, null, "Awaiting plan", null)
            {
                MovementKey = $"order:{order.Id:N}:{(direction == "Inbound" ? "in" : "out")}",
                Source = "Approved order"
            });
        }

        // Live ETAs from tracking (Samsara) for our own trucks heading into SLH.
        var warehouseStopIds = loads.SelectMany(load => load.Stops).Where(stop => MatchesWarehouse(stop.Name, warehouseNames)).Select(stop => stop.Id).ToList();
        Dictionary<Guid, EtaSnapshot> etas = warehouseStopIds.Count == 0 ? [] : (await db.EtaSnapshots.AsNoTracking()
                .Where(x => warehouseStopIds.Contains(x.StopId))
                .ToListAsync(ct))
            .GroupBy(x => x.StopId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(item => item.CapturedAtUtc).First());
        inbound = inbound.Select(row => WithEta(row, loads, warehouseNames, etas)).ToList();
        outbound = outbound.Select(row => WithEta(row, loads, warehouseNames, etas)).ToList();

        // Customers bringing goods to SLH themselves (e.g. Emery "delivered to your site today
        // pm"): warehouse-only inbound, not a job for the planners.
        inbound.AddRange(await DropOffsAsync(date, ct));

        var checks = await ChecksAsync(date, ct);
        inbound = inbound.Select(row => WithChecks(row, checks)).OrderBy(x => x.RunReference).ThenBy(x => x.Customer).ToList();
        outbound = outbound.Select(row => WithChecks(row, checks)).OrderBy(x => x.RunReference).ThenBy(x => x.Customer).ToList();
        return new(date, inbound, outbound, new(inbound.Count, outbound.Count, inbound.Sum(x => x.PlannedPallets), outbound.Sum(x => x.PlannedPallets)));
    }

    /// <summary>Record what the warehouse actually received or loaded. Append-only, so every
    /// count and correction stays in the audit trail.</summary>
    public async Task<WarehouseCheck> RecordCheckAsync(WarehouseCheckRequest request, string? user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MovementKey)) throw new ArgumentException("movementKey is required.");
        var direction = string.Equals(request.Direction, "Outbound", StringComparison.OrdinalIgnoreCase) ? "Outbound" : "Inbound";
        var check = new WarehouseCheck(request.MovementKey.Trim(), request.Date, direction,
            direction == "Inbound" ? "Received" : "Loaded", request.ActualQuantity, string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            user, DateTimeOffset.UtcNow);
        db.StagedImports.Add(new StagedImport
        {
            EntityType = CheckEntityType,
            IdempotencyKey = $"warehousecheck:{Guid.NewGuid():N}",
            Status = StagingStatus.Promoted,
            Source = "Warehouse board",
            PayloadJson = JsonSerializer.Serialize(check, JsonOptions),
            ReviewedBy = user,
            ReviewedAtUtc = check.RecordedAtUtc
        });
        await db.SaveChangesAsync(ct);
        return check;
    }

    private const string CheckEntityType = "warehousecheck";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<Dictionary<string, List<WarehouseCheck>>> ChecksAsync(DateOnly date, CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == CheckEntityType)
            .OrderByDescending(x => x.ReceivedAtUtc)
            .Take(5000)
            .Select(x => x.PayloadJson)
            .ToListAsync(ct);
        var result = new Dictionary<string, List<WarehouseCheck>>(StringComparer.Ordinal);
        foreach (var json in rows)
        {
            try
            {
                var check = JsonSerializer.Deserialize<WarehouseCheck>(json, JsonOptions);
                if (check is null || check.Date != date) continue;
                if (!result.TryGetValue(check.MovementKey, out var list)) result[check.MovementKey] = list = [];
                list.Add(check);
            }
            catch (JsonException) { }
        }
        foreach (var list in result.Values) list.Sort((a, b) => b.RecordedAtUtc.CompareTo(a.RecordedAtUtc));
        return result;
    }

    private static WarehouseMovementRow WithChecks(WarehouseMovementRow row, Dictionary<string, List<WarehouseCheck>> checks)
    {
        var key = row.MovementKey ?? DefaultKey(row);
        if (!checks.TryGetValue(key, out var history)) return row with { MovementKey = key };
        var latest = history[0];
        return row with
        {
            MovementKey = key,
            Difference = latest.ActualQuantity - row.PlannedPallets,
            ActualQuantity = latest.ActualQuantity,
            CheckedAtUtc = latest.RecordedAtUtc,
            CheckedBy = latest.RecordedBy,
            CheckHistory = history
        };
    }

    private static string DefaultKey(WarehouseMovementRow row) =>
        $"load:{row.LoadId:N}:{Normalize(row.LoadReference ?? row.PoReference ?? row.Customer)}:{row.Direction.ToLowerInvariant()}";

    private static WarehouseMovementRow WithEta(WarehouseMovementRow row, List<Load> loads, IReadOnlySet<string> warehouseNames, Dictionary<Guid, EtaSnapshot> etas)
    {
        var load = loads.FirstOrDefault(x => x.Id == row.LoadId);
        var stop = load?.Stops.OrderBy(x => x.Sequence).FirstOrDefault(x => MatchesWarehouse(x.Name, warehouseNames));
        if (stop is null || !etas.TryGetValue(stop.Id, out var eta) || eta.EtaUtc is null) return row;
        return row with { LiveEtaUtc = eta.EtaUtc, EtaRisk = eta.Risk, EtaCapturedAtUtc = eta.CapturedAtUtc, EtaSource = eta.Source };
    }

    private async Task<List<WarehouseMovementRow>> DropOffsAsync(DateOnly date, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-21);
        var staged = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == "order" && x.ReceivedAtUtc >= since &&
                        (x.Status == StagingStatus.PendingReview || x.Status == StagingStatus.Approved || x.Status == StagingStatus.Promoted) &&
                        x.PayloadJson.Contains("warehouseDropOff"))
            .ToListAsync(ct);
        var rows = new List<WarehouseMovementRow>();
        foreach (var item in staged)
        {
            try
            {
                using var document = JsonDocument.Parse(item.PayloadJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("warehouseDropOff", out var dropOff) || dropOff.ValueKind != JsonValueKind.Object) continue;
                if (!DateOnly.TryParse(Text(dropOff, "date"), out var dropDate) || dropDate != date) continue;
                int.TryParse(Text(root, "pallets"), out var pallets);
                var from = Text(dropOff, "from") ?? Text(root, "customerName") ?? "Customer";
                var expected = Text(dropOff, "expected");
                rows.Add(new WarehouseMovementRow("Inbound", Guid.Empty, "Customer drop-off", expected ?? "Unallocated", null, null, null,
                    Text(root, "customerCode") ?? "Unknown", from, Text(root, "sellerName"), Text(root, "customerPo") ?? Text(root, "poNumber"),
                    null, Text(root, "palletType"), pallets, Text(root, "temperatureRequirement"), dropDate, null, null,
                    item.Status == StagingStatus.PendingReview ? "Booking in review" : "Booked", null)
                {
                    MovementKey = $"dropoff:{item.Id:N}",
                    Source = $"{from} drop-off",
                    OnwardTo = Text(root, "stallNumber")
                });
            }
            catch (JsonException) { }
        }
        return rows;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null }
            : null;

    private async Task<List<WarehouseAllocation>> ReadLatestAllocations(List<Guid> loadIds, DateOnly[] dates, CancellationToken ct)
    {
        if (loadIds.Count == 0) return [];
        var texts = dates.Select(d => d.ToString("yyyy-MM-dd")).ToArray();
        var query = db.StagedImports.AsNoTracking().Where(x => x.EntityType == "planningpalletallocation" && x.Status == StagingStatus.Promoted);
        // Only rows for these days (each allocation records its date).
        query = texts.Length == 2
            ? query.Where(x => x.PayloadJson.Contains(texts[0]) || x.PayloadJson.Contains(texts[1]))
            : query.Where(x => x.PayloadJson.Contains(texts[0]));
        var rows = await query.OrderByDescending(x => x.ReceivedAtUtc).Take(5000).Select(x => x.PayloadJson).ToListAsync(ct);
        var wanted = loadIds.ToHashSet();
        var latest = new Dictionary<(Guid, Guid, Guid?), WarehouseAllocation>();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        foreach (var json in rows)
        {
            try
            {
                var allocation = JsonSerializer.Deserialize<WarehouseAllocation>(json, options);
                if (allocation is null || !dates.Contains(allocation.Date) || !wanted.Contains(allocation.LoadId)) continue;
                latest.TryAdd((allocation.LoadId, allocation.OrderId, allocation.SourceLineId), allocation);
            }
            catch (JsonException) { }
        }
        return latest.Values.ToList();
    }

    private async Task<List<TransportOrder>> ReadOrdersAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new Dictionary<string, TransportOrder>(StringComparer.OrdinalIgnoreCase);
        foreach (var order in await db.TransportOrders.AsNoTracking().Where(x => x.CollectionDate >= from && x.CollectionDate <= to).Take(4000).ToListAsync(ct))
            result[order.Reference] = order;
        foreach (var order in await PlanningRegisterStore.ReadOrdersAsync(db, from, to, ct))
            result.TryAdd(order.Reference, order);
        return result.Values.ToList();
    }

    private static bool MatchesWarehouse(string? value, IReadOnlySet<string> warehouseNames)
    {
        var normalized = Normalize(value);
        return normalized.Length > 0 && warehouseNames.Any(alias => normalized == alias || normalized.Contains(alias, StringComparison.Ordinal));
    }
    private sealed record WarehouseAllocation(Guid OrderId, Guid LoadId, int Pallets, DateOnly Date, DateTimeOffset UpdatedAtUtc, string? UpdatedBy, Guid? SourceLineId);
}

public sealed record WarehouseMovementRow(string Direction, Guid LoadId, string RunReference, string Period, string? Driver, string? Vehicle,
    string? Trailer, string Customer, string? From, string? To, string? PoReference, string? LoadReference,
    string? PalletType, int PlannedPallets, string? Temperature, DateOnly? DueDate, TimeOnly? DueTime,
    DateTimeOffset? ExpectedAtUtc, string Status, int? Difference)
{
    public string? MovementKey { get; init; }
    public string? Source { get; init; }
    public string? OnwardTo { get; init; }
    public DateTimeOffset? LiveEtaUtc { get; init; }
    public string? EtaRisk { get; init; }
    public DateTimeOffset? EtaCapturedAtUtc { get; init; }
    public string? EtaSource { get; init; }
    public int? ActualQuantity { get; init; }
    public DateTimeOffset? CheckedAtUtc { get; init; }
    public string? CheckedBy { get; init; }
    public IReadOnlyList<WarehouseCheck>? CheckHistory { get; init; }
}
public sealed record WarehouseCheck(string MovementKey, DateOnly Date, string Direction, string Action, int ActualQuantity, string? Note,
    string? RecordedBy, DateTimeOffset RecordedAtUtc);
public sealed record WarehouseCheckRequest(string MovementKey, DateOnly Date, string Direction, int ActualQuantity, string? Note);
public sealed record WarehouseDailyTotals(int InboundRows, int OutboundRows, int InboundPallets, int OutboundPallets);
public sealed record WarehouseDailyResult(DateOnly PlanningDate, IReadOnlyList<WarehouseMovementRow> Inbound,
    IReadOnlyList<WarehouseMovementRow> Outbound, WarehouseDailyTotals Totals);
