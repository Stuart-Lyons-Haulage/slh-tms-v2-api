using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class PlanningRegisterStore
{
    private const string LoadType = "planningload";
    private const int RecentLoadRegisterWindow = 10000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static async Task<List<TransportOrder>> ReadOrdersAsync(TmsDbContext db, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => (x.EntityType == "order" || x.EntityType == "register:order") &&
                (x.Status == StagingStatus.Approved || x.Status == StagingStatus.Promoted))
            .OrderByDescending(x => x.ReceivedAtUtc).Take(5000).ToListAsync(ct);
        var orders = new Dictionary<string, TransportOrder>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var order = ParseOrder(row);
            if (order is null || orders.ContainsKey(order.Reference)) continue;
            if (from is not null && order.CollectionDate < from.Value || to is not null && order.CollectionDate > to.Value) continue;
            orders[order.Reference] = order;
        }
        return orders.Values.OrderBy(x => x.CollectionDate).ThenBy(x => x.Reference).Take(1000).ToList();
    }

    public static async Task<List<Load>> ReadLoadsAsync(TmsDbContext db, DateOnly? date, CancellationToken ct)
    {
        // Planning loads live in an audited JSON register, so PlanningDate cannot be safely
        // filtered in SQL across all supported database providers. Read the most recently
        // created/reviewed promoted rows first, then apply the planning-date filter after
        // deserialisation. The previous ascending Take(2000) selected the oldest register
        // rows before filtering, which could leave today's wallboard with only a live SQL
        // survivor even though the current imported plan contained many runs.
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == LoadType && x.Status == StagingStatus.Promoted)
            .OrderByDescending(x => x.ReviewedAtUtc ?? x.ReceivedAtUtc)
            .ThenByDescending(x => x.ReceivedAtUtc)
            .Take(RecentLoadRegisterWindow)
            .ToListAsync(ct);
        var loads = rows.Select(ParseLoad)
            .Where(x => x is not null && (date is null || x.PlanningDate == date))
            .Cast<Load>()
            .OrderBy(x => x.PlanningDate)
            .ThenBy(x => x.Reference)
            .Take(2000)
            .ToList();
        return loads;
    }

    public static async Task<Load?> GetLoadAsync(TmsDbContext db, Guid id, CancellationToken ct)
    {
        var key = $"planningload:{id:N}";
        var row = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x =>
            x.EntityType == LoadType
            && x.Status == StagingStatus.Promoted
            && x.IdempotencyKey == key, ct);
        return row is null ? null : ParseLoad(row);
    }

    public static async Task SaveLoadAsync(TmsDbContext db, Load load, string? user, CancellationToken ct)
    {
        foreach (var stop in load.Stops) stop.LoadId = load.Id;
        var key = $"planningload:{load.Id:N}";
        var row = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        var existingLoad = row is null ? null : ParseLoad(row);

        if (load.Status == LoadStatus.Completed && existingLoad?.Status != LoadStatus.Completed)
            await RunCompletionPersistenceGuard.EnsureCompletionEvidenceAsync(db, load.Id, ct);

        if (row is null)
        {
            row = new StagedImport { EntityType = LoadType, IdempotencyKey = key, PayloadJson = "{}", Source = "SLH planning register" };
            db.StagedImports.Add(row);
        }
        row.PayloadJson = JsonSerializer.Serialize(load, JsonOptions);
        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = DateTimeOffset.UtcNow;
        row.ReviewedBy = user;
        row.ReviewNote = "Saved in the audited planning register because dedicated planning tables are unavailable.";
        await PlanningAllocationStore.SyncSingleOrderRunAsync(db, load, user, ct);
        await db.SaveChangesAsync(ct);
    }

    private static Load? ParseLoad(StagedImport row)
    {
        try { return JsonSerializer.Deserialize<Load>(row.PayloadJson, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static TransportOrder? ParseOrder(StagedImport row)
    {
        try
        {
            using var document = JsonDocument.Parse(row.PayloadJson);
            var payload = document.RootElement;
            var reference = Text(payload, "poNumber") ?? Text(payload, "reference");
            var customer = Text(payload, "customerCode");
            if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(customer) || !DateOnly.TryParse(Text(payload, "collectionDate"), out var collectionDate)) return null;
            DateOnly? deliveryDate = DateOnly.TryParse(Text(payload, "deliveryDate"), out var delivery) ? delivery : null;
            DateTimeOffset? windowStart = DateTimeOffset.TryParse(Text(payload, "deliveryWindowStartUtc"), out var start) ? start : null;
            DateTimeOffset? windowEnd = DateTimeOffset.TryParse(Text(payload, "deliveryWindowEndUtc"), out var end) ? end : null;
            return new TransportOrder
            {
                Id = row.Id, Reference = Clip(reference, 80)!, CustomerCode = Clip(customer, 40)!, CollectionDate = collectionDate,
                DeliveryDate = deliveryDate, DeliveryWindowStartUtc = windowStart, DeliveryWindowEndUtc = windowEnd,
                Pallets = int.TryParse(Text(payload, "pallets"), out var pallets) ? pallets : null,
                SellerName = Clip(Text(payload, "sellerName"), 200), MarketName = Clip(Text(payload, "marketName"), 80),
                StallNumber = Clip(Text(payload, "stallNumber"), 200), DriverInstructions = Clip(Text(payload, "driverInstructions"), 1000),
                MapLink = Clip(Text(payload, "mapLink"), 1000), Status = OrderStatus.ReadyToPlan, CreatedAtUtc = row.ReceivedAtUtc
            };
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement payload, string name)
    {
        foreach (var property in payload.EnumerateObject())
            if (string.Equals(new string(property.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()), new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()), StringComparison.Ordinal))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : property.Value.ToString();
        return null;
    }

    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
