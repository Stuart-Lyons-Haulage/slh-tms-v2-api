using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/planning-control")]
[Authorize]
public sealed class PlanningControlFastController(TmsDbContext db) : ControllerBase
{
    private const string AllocationType = "planningpalletallocation";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    [HttpGet("orders/day")]
    public async Task<IActionResult> OrdersForDay([FromQuery] DateOnly date, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

        var from = date.AddDays(-1);
        var to = date.AddDays(1);
        var dateTokens = Enumerable.Range(0, 3).Select(offset => from.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();

        var orders = await ReadOperationalOrders(date, from, to, ct);
        var details = await ReadApprovedOrderDetails(date, dateTokens, ct);
        var allocations = await ReadAllocations(date, ct);
        var rows = new List<object>();
        var amCount = 0;
        var pmCount = 0;
        var orderedTotal = 0;
        var plannedTotal = 0;

        foreach (var order in orders.Where(item => item.Status != OrderStatus.Cancelled).OrderBy(item => item.CollectionDate).ThenBy(item => item.Reference))
        {
            details.TryGetValue(Normalise(order.Reference), out var detail);
            var ordered = Math.Max(order.Pallets ?? detail?.Pallets ?? 0, 0);
            var planned = allocations.Where(item => item.OrderId == order.Id).Sum(item => Math.Max(item.Pallets, 0));
            var window = ResolveWindow(order, detail);
            if (window == "PM") pmCount++; else amCount++;
            orderedTotal += ordered;
            plannedTotal += planned;

            rows.Add(new
            {
                order.Id,
                order.Reference,
                order.CustomerCode,
                order.CollectionDate,
                order.DeliveryDate,
                orderedPallets = ordered,
                plannedPallets = planned,
                outstandingPallets = Math.Max(ordered - planned, 0),
                collection = detail?.Collection ?? order.SellerName ?? "Collection not mapped",
                destination = detail?.Destination ?? order.StallNumber ?? order.MarketName ?? "Destination not mapped",
                planningWindow = window,
                planningGroup = window == "PM" ? "PM Work" : "AM Runs",
                runsOvernight = RunsOvernight(order, detail),
                source = detail?.Source,
                receivedAtUtc = detail?.UpdatedAtUtc ?? order.CreatedAtUtc
            });
        }

        return Ok(new
        {
            date,
            window = new { from, to },
            generatedAtUtc = DateTimeOffset.UtcNow,
            summary = new
            {
                ordered = orderedTotal,
                planned = plannedTotal,
                outstanding = Math.Max(orderedTotal - plannedTotal, 0),
                orders = rows.Count,
                amOrders = amCount,
                pmOrders = pmCount
            },
            planningGroups = new[] { "AM Runs", "PM Work" },
            orders = rows
        });
    }

    [HttpGet("orders/changes")]
    public async Task<IActionResult> Changes([FromQuery] DateOnly date, [FromQuery] DateTimeOffset? since, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        var marker = since ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        var dateText = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var changed = await db.StagedImports.AsNoTracking()
            .Where(item => item.ReceivedAtUtc >= marker || item.ReviewedAtUtc >= marker)
            .Where(item => item.PayloadJson.Contains(dateText))
            .OrderByDescending(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .Take(100)
            .Select(item => new { item.Id, item.EntityType, item.Status, item.ReceivedAtUtc, item.ReviewedAtUtc })
            .ToListAsync(ct);
        return Ok(new { date, since = marker, changed = changed.Count > 0, count = changed.Count, items = changed });
    }

    private async Task<List<TransportOrder>> ReadOperationalOrders(DateOnly date, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new Dictionary<string, TransportOrder>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var primary = await db.TransportOrders.AsNoTracking()
                .Where(item => item.CollectionDate >= from && item.CollectionDate <= to)
                .OrderBy(item => item.CollectionDate).ThenBy(item => item.Reference)
                .Take(1000)
                .ToListAsync(ct);
            foreach (var order in primary)
                result[order.Reference] = order;
        }
        catch { db.ChangeTracker.Clear(); }

        foreach (var order in await PlanningRegisterStore.ReadOrdersAsync(db, date, date, ct))
            result.TryAdd(order.Reference, order);

        return result.Values.ToList();
    }

    private async Task<Dictionary<string, FastOrderDetail>> ReadApprovedOrderDetails(DateOnly date, string[] dateTokens, CancellationToken ct)
    {
        var query = db.StagedImports.AsNoTracking()
            .Where(item => (item.EntityType == "order" || item.EntityType == "register:order") &&
                (item.Status == StagingStatus.Approved || item.Status == StagingStatus.Promoted));

        if (dateTokens.Length >= 3)
        {
            var previousText = dateTokens[0];
            var dateText = dateTokens[1];
            var nextText = dateTokens[2];
            query = query.Where(item => item.PayloadJson.Contains(previousText) || item.PayloadJson.Contains(dateText) || item.PayloadJson.Contains(nextText));
        }

        var rows = await query
            .OrderByDescending(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .ThenByDescending(item => item.ReceivedAtUtc)
            .Take(1500)
            .ToListAsync(ct);

        var result = new Dictionary<string, FastOrderDetail>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                var reference = Text(root, "poNumber", "reference", "orderReference", "orderRef");
                if (string.IsNullOrWhiteSpace(reference) || result.ContainsKey(Normalise(reference))) continue;
                if (!PlanningDates(root).Contains(date)) continue;
                result[Normalise(reference)] = new FastOrderDetail(
                    reference,
                    Text(root, "collectionLocation", "collectionSite", "collection", "sellerName", "pickupLocation", "pickupSite"),
                    Text(root, "deliveryLocation", "deliverySite", "delivery", "destination", "depot", "stallNumber"),
                    Text(root, "planningGroup", "palletOrderGroup", "collectionGroup"),
                    Text(root, "temperature", "temperatureC", "temp", "temperatureRequirement"),
                    Text(root, "unitType", "capacityType", "palletType", "palletName", "palletFormat", "pallet"),
                    Int(root, "pallets", "palletQty", "palletQuantity", "quantity"),
                    row.Source,
                    row.ReviewedAtUtc ?? row.ReceivedAtUtc,
                    Text(root, "planningWindow", "suggestedPlanningWindow"),
                    Text(root, "suggestedRouteType", "routeTiming"),
                    Text(root, "planningWindowReason", "pmReason"),
                    Bool(root, "runsOvernight", "overnightRoute"));
            }
            catch (JsonException) { }
        }
        return result;
    }

    private async Task<List<FastAllocationState>> ReadAllocations(DateOnly date, CancellationToken ct)
    {
        var dateText = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == AllocationType && item.Status == StagingStatus.Promoted && item.PayloadJson.Contains(dateText))
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(5000)
            .ToListAsync(ct);

        var latest = new Dictionary<(Guid OrderId, Guid LoadId, Guid? SourceLineId), FastAllocationState>();
        foreach (var row in rows)
        {
            try
            {
                var state = JsonSerializer.Deserialize<FastAllocationState>(row.PayloadJson, JsonOptions);
                if (state is null || state.Date != date) continue;
                latest.TryAdd((state.OrderId, state.LoadId, state.SourceLineId), state);
            }
            catch (JsonException) { }
        }
        return latest.Values.ToList();
    }

    private static IReadOnlyCollection<DateOnly> PlanningDates(JsonElement root)
    {
        var dates = new HashSet<DateOnly>();
        var collection = Date(root, "collectionDate");
        var delivery = Date(root, "deliveryDate");
        var window = CanonicalWindow(Text(root, "suggestedPlanningWindow", "planningWindow", "routeTiming"));
        var overnight = Bool(root, "runsOvernight", "overnightRoute") == true ||
            collection is DateOnly c && delivery is DateOnly d && c < d;

        if (collection is DateOnly collectionDate && delivery is DateOnly deliveryDate && collectionDate < deliveryDate)
            return [collectionDate];
        if (window == "PM" && overnight && collection is DateOnly overnightCollection)
            return [overnightCollection];
        if (window == "PM" && collection is null && delivery is DateOnly deliveryOnly)
            return [deliveryOnly.AddDays(-1), deliveryOnly];
        if (collection is DateOnly c2) dates.Add(c2);
        if (delivery is DateOnly d2) dates.Add(d2);
        return dates;
    }

    private static string ResolveWindow(TransportOrder order, FastOrderDetail? detail)
    {
        var explicitWindow = CanonicalWindow(detail?.PlanningWindow ?? detail?.SuggestedRouteType ?? detail?.PlanningWindowReason);
        if (explicitWindow == "PM") return "PM";
        if (RunsOvernight(order, detail)) return "PM";
        return "AM";
    }

    private static bool RunsOvernight(TransportOrder order, FastOrderDetail? detail) =>
        detail?.RunsOvernight == true || order.DeliveryDate is DateOnly delivery && delivery > order.CollectionDate;

    private static string CanonicalWindow(string? value)
    {
        var normal = Normalise(value);
        return normal.Contains("PM", StringComparison.OrdinalIgnoreCase) ||
               normal.Contains("OVERNIGHT", StringComparison.OrdinalIgnoreCase) ||
               normal.Contains("MARKET", StringComparison.OrdinalIgnoreCase) ||
               normal.Contains("TRANSFER", StringComparison.OrdinalIgnoreCase)
            ? "PM"
            : "AM";
    }

    private static string Normalise(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? Text(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(payload, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!.Trim();
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return null;
    }

    private static int? Int(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(payload, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        return null;
    }

    private static bool? Bool(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(payload, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static DateOnly? Date(JsonElement payload, string name)
    {
        var text = Text(payload, name);
        return DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    private static bool TryGet(JsonElement payload, string name, out JsonElement value)
    {
        if (payload.TryGetProperty(name, out value)) return true;
        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private sealed record FastOrderDetail(
        string Reference,
        string? Collection,
        string? Destination,
        string? Group,
        string? Temperature,
        string? PalletType,
        int? Pallets,
        string? Source,
        DateTimeOffset UpdatedAtUtc,
        string? PlanningWindow,
        string? SuggestedRouteType,
        string? PlanningWindowReason,
        bool? RunsOvernight);

    private sealed record FastAllocationState(Guid OrderId, Guid LoadId, int Pallets, DateOnly Date, DateTimeOffset UpdatedAtUtc, string? UpdatedBy, Guid? SourceLineId = null);
}
