using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
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

        var loads = await db.Loads.AsNoTracking().Include(x => x.Stops).Where(x => x.PlanningDate == date && x.Status != LoadStatus.Cancelled).ToListAsync(ct);
        var loadIds = loads.Select(x => x.Id).ToList();
        var allocations = await ReadLatestAllocations(loadIds, date, ct);
        var lineIds = allocations.Where(x => x.SourceLineId is not null).Select(x => x.SourceLineId!.Value).Distinct().ToList();
        var lines = await db.OrderSourceLines.AsNoTracking().Where(x => lineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var revisionIds = lines.Values.Select(x => x.RevisionId).Distinct().ToList();
        var revisions = await db.OrderRevisions.AsNoTracking().Where(x => revisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var movementIds = revisions.Values.Select(x => x.MovementId).Distinct().ToList();
        var linkedOrders = await db.TransportOrders.AsNoTracking().Where(x => x.SourceMovementId != null).ToListAsync(ct);
        var orders = linkedOrders.Where(x => movementIds.Contains(x.SourceMovementId!.Value)).ToList();
        var orderByMovement = orders.GroupBy(x => x.SourceMovementId!.Value).ToDictionary(x => x.Key, x => x.First());
        var vehicleIds = loads.Where(x => x.VehicleId is not null).Select(x => x.VehicleId!.Value).Distinct().ToList();
        var trailerIds = loads.Where(x => x.TrailerId is not null).Select(x => x.TrailerId!.Value).Distinct().ToList();
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => vehicleIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var trailers = await db.Trailers.AsNoTracking().Where(x => trailerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var driverIds = loads.Where(x => x.DriverId is not null).Select(x => x.DriverId!.Value).Distinct().ToList();
        var drivers = await db.Drivers.AsNoTracking().Where(x => driverIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        var inbound = new List<WarehouseMovementRow>();
        var outbound = new List<WarehouseMovementRow>();
        foreach (var allocation in allocations.Where(x => x.Pallets > 0 && x.SourceLineId is not null))
        {
            if (!lines.TryGetValue(allocation.SourceLineId!.Value, out var line) || !revisions.TryGetValue(line.RevisionId, out var revision)) continue;
            orderByMovement.TryGetValue(revision.MovementId, out var order);
            var load = loads.Single(x => x.Id == allocation.LoadId);
            var warehouseStop = load.Stops.OrderBy(x => x.Sequence).FirstOrDefault(x => MatchesWarehouse(x.Name, warehouseNames));
            if (warehouseStop is null) continue;
            var direction = MatchesWarehouse(line.DeliverySite, warehouseNames) ? "Inbound"
                : MatchesWarehouse(line.CollectionSite, warehouseNames) ? "Outbound" : null;
            if (direction is null) continue;
            var row = new WarehouseMovementRow(direction, load.Id, load.Reference, Period(line.CollectionTimeFrom),
                load.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var driver) ? driver.DisplayName : null,
                load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var vehicle) ? vehicle.Registration : null,
                load.TrailerId is Guid trailerId && trailers.TryGetValue(trailerId, out var trailer) ? trailer.TrailerNumber : null,
                order?.CustomerCode ?? "Unknown", line.CollectionSite, line.DeliverySite, order?.Reference,
                line.LoadReference, line.PalletType, allocation.Pallets, line.TemperatureRequirement,
                direction == "Inbound" ? line.DeliveryDate : line.CollectionDate,
                direction == "Inbound" ? null : line.CollectionTimeFrom, warehouseStop.PlannedArrivalUtc, load.Status.ToString(), null)
            {
                MovementKey = $"load:{load.Id:N}:line:{line.Id:N}:{direction.ToLowerInvariant()}",
                Source = "Planned run"
            };
            (direction == "Inbound" ? inbound : outbound).Add(row);
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

        // Approved work leaving SLH that is not on a run yet, so the warehouse can see it coming.
        var allocatedOrderIds = allocations.Select(x => x.OrderId).ToHashSet();
        var unplannedOutbound = (await db.TransportOrders.AsNoTracking()
                .Where(x => x.CollectionDate == date && x.Status != OrderStatus.Cancelled)
                .ToListAsync(ct))
            .Where(order => !allocatedOrderIds.Contains(order.Id) && MatchesWarehouse(order.SellerName, warehouseNames))
            .Select(order => new WarehouseMovementRow("Outbound", Guid.Empty, "Not on a run yet", "Unallocated", null, null, null,
                order.CustomerCode, order.SellerName, order.StallNumber, order.Reference, null, null, order.Pallets ?? 0, null,
                order.CollectionDate, null, null, "Awaiting plan", null) { MovementKey = $"order:{order.Id:N}:out", Source = "Approved order" })
            .ToList();
        outbound.AddRange(unplannedOutbound);

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

    private async Task<List<WarehouseAllocation>> ReadLatestAllocations(List<Guid> loadIds, DateOnly date, CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking().Where(x => x.EntityType == "planningpalletallocation" && x.Status == StagingStatus.Promoted).OrderByDescending(x => x.ReceivedAtUtc).Take(20000).ToListAsync(ct);
        var latest = new Dictionary<(Guid, Guid?), WarehouseAllocation>();
        foreach (var row in rows)
        {
            try
            {
                var allocation = JsonSerializer.Deserialize<WarehouseAllocation>(row.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
                if (allocation is null || allocation.Date != date || !loadIds.Contains(allocation.LoadId)) continue;
                var key = (allocation.LoadId, allocation.SourceLineId);
                if (!latest.ContainsKey(key)) latest[key] = allocation;
            }
            catch (JsonException) { }
        }
        return latest.Values.ToList();
    }

    private static string Period(TimeOnly? time) => time is null ? "Unallocated" : time.Value.Hour < 17 ? "AM" : "PM";
    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static HashSet<string> WarehouseNames(Site canonical, IReadOnlyCollection<IntegrationMapping> mappings)
    {
        var names = new[] { canonical.Name, canonical.ExternalCode, canonical.DriverTextName, canonical.Aliases, "Barnham Coldstore", "Stuart Lyons Distribution", "Stuart Lions Distribution" }
            .Concat(mappings.SelectMany(x => new[] { x.ExternalKey, x.ExternalLabel, x.NormalizedExternalValue }));
        return names.Where(x => !string.IsNullOrWhiteSpace(x))
            .SelectMany(x => x!.Split(new char[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(Normalize).Where(x => x.Length > 2).ToHashSet(StringComparer.Ordinal);
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
