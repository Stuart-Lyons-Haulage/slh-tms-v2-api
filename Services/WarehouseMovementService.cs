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
                direction == "Inbound" ? null : line.CollectionTimeFrom, warehouseStop.PlannedArrivalUtc, load.Status.ToString(), null);
            (direction == "Inbound" ? inbound : outbound).Add(row);
        }
        inbound = inbound.OrderBy(x => x.RunReference).ThenBy(x => x.Customer).ToList();
        outbound = outbound.OrderBy(x => x.RunReference).ThenBy(x => x.Customer).ToList();
        return new(date, inbound, outbound, new(inbound.Count, outbound.Count, inbound.Sum(x => x.PlannedPallets), outbound.Sum(x => x.PlannedPallets)));
    }

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
    DateTimeOffset? ExpectedAtUtc, string Status, int? Difference);
public sealed record WarehouseDailyTotals(int InboundRows, int OutboundRows, int InboundPallets, int OutboundPallets);
public sealed record WarehouseDailyResult(DateOnly PlanningDate, IReadOnlyList<WarehouseMovementRow> Inbound,
    IReadOnlyList<WarehouseMovementRow> Outbound, WarehouseDailyTotals Totals);
