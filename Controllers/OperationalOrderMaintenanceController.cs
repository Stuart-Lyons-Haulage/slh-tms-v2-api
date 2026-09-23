using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Resilient order amendment endpoint used by Manage Jobs. It understands both the
/// primary TransportOrders table and the audited planning-register fallback used by
/// the portal when legacy planning tables are unavailable.
/// </summary>
[ApiController]
[Route("api/v1/operational-recovery/orders")]
[Authorize(Policy = "TmsWrite")]
public sealed class OperationalOrderMaintenanceController(
    TmsDbContext db,
    ILogger<OperationalOrderMaintenanceController> logger) : ControllerBase
{
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] OperationalOrderUpdateRequest request, CancellationToken ct)
    {
        var reference = Clip(request.Reference, 80);
        var customerCode = Clip(request.CustomerCode, 40);
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(customerCode))
            return BadRequest(new { message = "Order reference and customer are required." });
        if (request.Pallets is < 0)
            return BadRequest(new { message = "Pallet quantity cannot be negative." });

        var order = await db.TransportOrders.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (order is not null)
        {
            if (order.Status == OrderStatus.Cancelled)
                return BadRequest(new { message = "A cancelled order cannot be amended." });

            var previousAddress = ExtractTagged(order.DriverInstructions, "Delivery address");
            var deliveryAddress = Clip(request.DeliveryAddress, 500);
            var collectionSite = Clip(request.CollectionSite, 200);
            var depotId = Clip(request.DepotId, 80);
            var destination = Clip(request.Destination, 200);

            order.Reference = reference!;
            order.CustomerCode = customerCode!;
            order.CollectionDate = request.CollectionDate;
            order.DeliveryDate = request.DeliveryDate;
            order.DeliveryWindowStartUtc = request.DeliveryWindowStartUtc;
            order.DeliveryWindowEndUtc = request.DeliveryWindowEndUtc;
            order.Pallets = request.Pallets;
            order.SellerName = collectionSite;
            order.MarketName = depotId;
            order.StallNumber = destination;
            order.DriverInstructions = BuildNotes(collectionSite, depotId, destination, deliveryAddress,
                Clip(request.CustomerRef, 200), Clip(request.PoRef, 200), Clip(request.UnitType, 80),
                Clip(request.PalletName, 200), Clip(request.Notes, 1000));
            order.MapLink = string.IsNullOrWhiteSpace(deliveryAddress)
                ? Clip(request.MapLink, 1000)
                : $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(deliveryAddress)}";

            try
            {
                var linkedStops = await db.LoadStops.Where(stop => stop.OrderId == id).ToListAsync(ct);
                foreach (var stop in linkedStops)
                {
                    stop.Name = Clip($"{order.CustomerCode} · {destination ?? depotId ?? order.Reference}", 200)!;
                    if (!string.Equals(previousAddress, deliveryAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        stop.Address = deliveryAddress;
                        stop.Latitude = null;
                        stop.Longitude = null;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Best-effort linked-stop amendment failed for order {OrderId}.", id);
                db.ChangeTracker.Clear();
                order = await db.TransportOrders.SingleAsync(item => item.Id == id, ct);
                order.Reference = reference!;
                order.CustomerCode = customerCode!;
                order.CollectionDate = request.CollectionDate;
                order.DeliveryDate = request.DeliveryDate;
                order.DeliveryWindowStartUtc = request.DeliveryWindowStartUtc;
                order.DeliveryWindowEndUtc = request.DeliveryWindowEndUtc;
                order.Pallets = request.Pallets;
                order.SellerName = collectionSite;
                order.MarketName = depotId;
                order.StallNumber = destination;
                order.DriverInstructions = BuildNotes(collectionSite, depotId, destination, deliveryAddress,
                    Clip(request.CustomerRef, 200), Clip(request.PoRef, 200), Clip(request.UnitType, 80),
                    Clip(request.PalletName, 200), Clip(request.Notes, 1000));
                order.MapLink = string.IsNullOrWhiteSpace(deliveryAddress)
                    ? Clip(request.MapLink, 1000)
                    : $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(deliveryAddress)}";
            }

            await db.SaveChangesAsync(ct);
            return Ok(new { order.Id, order.Reference, order.CustomerCode, source = "TransportOrders" });
        }

        var register = await db.StagedImports.SingleOrDefaultAsync(item => item.Id == id &&
            (item.EntityType == "order" || item.EntityType == "register:order"), ct);
        if (register is null)
            return NotFound(new { message = "Order was not found in either the primary order table or the fallback planning register." });

        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(register.PayloadJson)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            return BadRequest(new { message = "The fallback order payload is invalid JSON and cannot be amended safely." });
        }

        Set(payload, "reference", reference);
        Set(payload, "poNumber", reference);
        Set(payload, "customerCode", customerCode);
        Set(payload, "collectionDate", request.CollectionDate.ToString("yyyy-MM-dd"));
        Set(payload, "deliveryDate", request.DeliveryDate?.ToString("yyyy-MM-dd"));
        Set(payload, "pallets", request.Pallets);
        Set(payload, "sellerName", Clip(request.CollectionSite, 200));
        Set(payload, "marketName", Clip(request.DepotId, 80));
        Set(payload, "stallNumber", Clip(request.Destination, 200));
        Set(payload, "collectionSite", Clip(request.CollectionSite, 200));
        Set(payload, "depotId", Clip(request.DepotId, 80));
        Set(payload, "destination", Clip(request.Destination, 200));
        Set(payload, "deliveryAddress", Clip(request.DeliveryAddress, 500));
        Set(payload, "customerRef", Clip(request.CustomerRef, 200));
        Set(payload, "poRef", Clip(request.PoRef, 200));
        Set(payload, "unitType", Clip(request.UnitType, 80));
        Set(payload, "palletType", Clip(request.UnitType, 80));
        Set(payload, "palletName", Clip(request.PalletName, 200));
        Set(payload, "notes", Clip(request.Notes, 1000));
        Set(payload, "mapLink", string.IsNullOrWhiteSpace(request.DeliveryAddress)
            ? Clip(request.MapLink, 1000)
            : $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(request.DeliveryAddress.Trim())}");

        register.PayloadJson = payload.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        register.ReviewedAtUtc = DateTimeOffset.UtcNow;
        register.ReviewedBy = User.Identity?.Name;
        register.ReviewNote = "Amended from Manage Jobs; audited source payload retained in the planning register.";
        await db.SaveChangesAsync(ct);

        return Ok(new { id, reference, customerCode, source = "PlanningRegister" });
    }

    private static void Set(JsonObject payload, string name, string? value)
    {
        if (value is null) payload.Remove(name); else payload[name] = value;
    }

    private static void Set(JsonObject payload, string name, int? value)
    {
        if (value is null) payload.Remove(name); else payload[name] = value.Value;
    }

    private static string BuildNotes(string? collectionSite, string? depotId, string? destination, string? deliveryAddress,
        string? customerRef, string? poRef, string? unitType, string? palletName, string? notes)
    {
        var value = string.Join(" · ", new[]
        {
            Tag("Collection site", collectionSite), Tag("Depot ID", depotId), Tag("Depot", destination),
            Tag("Delivery address", deliveryAddress), Tag("Customer ref", customerRef), Tag("PO ref", poRef),
            Tag("Unit type", unitType), Tag("Pallet", palletName), notes
        }.Where(item => !string.IsNullOrWhiteSpace(item)));
        return value.Length <= 1000 ? value : value[..1000];
    }

    private static string? ExtractTagged(string? notes, string label)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var prefix = $"{label}:";
        return notes.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();
    }

    private static string? Tag(string label, string? value) => string.IsNullOrWhiteSpace(value) ? null : $"{label}: {value.Trim()}";
    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}

public sealed record OperationalOrderUpdateRequest(
    string Reference,
    string CustomerCode,
    DateOnly CollectionDate,
    DateOnly? DeliveryDate,
    DateTimeOffset? DeliveryWindowStartUtc,
    DateTimeOffset? DeliveryWindowEndUtc,
    int? Pallets,
    string? CollectionSite,
    string? DepotId,
    string? Destination,
    string? DeliveryAddress,
    string? CustomerRef,
    string? PoRef,
    string? UnitType,
    string? PalletName,
    string? Notes,
    string? MapLink);
