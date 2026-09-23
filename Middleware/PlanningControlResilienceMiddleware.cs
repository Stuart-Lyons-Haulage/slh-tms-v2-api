using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Middleware;

/// <summary>
/// Keeps the live Planner usable when production is temporarily behind the latest
/// optional planning/source-line schema. The normal controller remains authoritative;
/// this fallback is used only for a schema-related failure on the read-only pallet
/// planning snapshot endpoint.
/// </summary>
public sealed class PlanningControlResilienceMiddleware(RequestDelegate next, ILogger<PlanningControlResilienceMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, TmsDbContext db)
    {
        if (!HttpMethods.IsGet(context.Request.Method) ||
            !context.Request.Path.Equals("/api/v1/planning-control/pallets", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        try
        {
            await next(context);
        }
        catch (Exception exception) when (SchemaUnavailable(exception) && !context.Response.HasStarted)
        {
            logger.LogWarning(exception,
                "Planning-control pallet snapshot hit unavailable schema; returning resilient planning-register fallback.");

            db.ChangeTracker.Clear();
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

            if (!DateOnly.TryParse(context.Request.Query["date"].FirstOrDefault(), out var date))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { message = "A valid planning date is required." }, context.RequestAborted);
                return;
            }

            try
            {
                var orders = await ReadOrdersAsync(db, date, context.RequestAborted);
                var loads = await ReadLoadsAsync(db, date, context.RequestAborted);
                var liveLoads = loads.Where(load => load.Status != LoadStatus.Cancelled).OrderBy(load => load.Reference).ToList();

                var orderRows = orders
                    .Where(order => order.Status != OrderStatus.Cancelled)
                    .Select(order =>
                    {
                        var ordered = Math.Max(order.Pallets ?? 0, 0);
                        var linkedLoad = liveLoads.FirstOrDefault(load => load.Stops.Any(stop => stop.OrderId == order.Id));
                        var planned = linkedLoad is null ? 0 : ordered;
                        var collection = string.IsNullOrWhiteSpace(order.SellerName) ? "Collection not mapped" : order.SellerName;
                        var destination = string.IsNullOrWhiteSpace(order.StallNumber) ? "Destination not mapped" : order.StallNumber;
                        return new
                        {
                            order.Id,
                            order.Reference,
                            order.CustomerCode,
                            order.CollectionDate,
                            order.DeliveryDate,
                            order.DeliveryWindowStartUtc,
                            order.DeliveryWindowEndUtc,
                            orderedPallets = ordered,
                            plannedPallets = planned,
                            outstandingPallets = Math.Max(ordered - planned, 0),
                            overplannedPallets = 0,
                            collection,
                            destination,
                            planningGroup = collection,
                            temperature = (string?)null,
                            palletType = (string?)null,
                            source = "resilient-planning-register",
                            receivedAtUtc = order.CreatedAtUtc,
                            lateAddition = false,
                            sourceMovementId = order.SourceMovementId,
                            sourceLines = Array.Empty<object>(),
                            allocations = linkedLoad is null || ordered <= 0
                                ? Array.Empty<object>()
                                : new object[] { new { sourceLineId = (Guid?)null, loadId = linkedLoad.Id, loadReference = linkedLoad.Reference, pallets = ordered, updatedAtUtc = linkedLoad.CreatedAtUtc, updatedBy = "resilient-fallback" } }
                        };
                    })
                    .Where(row => row.orderedPallets > 0)
                    .ToList();

                var totalOrdered = orderRows.Sum(row => row.orderedPallets);
                var totalPlanned = orderRows.Sum(row => row.plannedPallets);
                var destinations = orderRows.Select(row => row.destination).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
                var planningGroups = orderRows.Select(row => row.planningGroup).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();

                await context.Response.WriteAsJsonAsync(new
                {
                    date,
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    degraded = true,
                    warning = "Optional planning schema is unavailable; showing resilient core planning data until schema repair completes.",
                    summary = new
                    {
                        ordered = totalOrdered,
                        planned = totalPlanned,
                        outstanding = Math.Max(totalOrdered - totalPlanned, 0),
                        overplanned = 0,
                        lateAdditions = 0,
                        orders = orderRows.Count,
                        runs = liveLoads.Count
                    },
                    planningGroups,
                    destinations,
                    cells = Array.Empty<object>(),
                    orders = orderRows,
                    runs = liveLoads.Select(load => new
                    {
                        load.Id,
                        load.Reference,
                        load.Status,
                        load.PalletSpacesUsed,
                        load.TotalPalletSpaces,
                        load.CapacityType,
                        stopCount = load.Stops.Count
                    }).ToList()
                }, context.RequestAborted);
            }
            catch (Exception fallbackException)
            {
                logger.LogError(fallbackException, "Resilient planning-control fallback also failed.");
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        message = "Planning data is temporarily unavailable while the production schema is being repaired.",
                        code = "PLANNING_SCHEMA_UNAVAILABLE"
                    }, context.RequestAborted);
                }
            }
        }
    }

    private static async Task<List<TransportOrder>> ReadOrdersAsync(TmsDbContext db, DateOnly date, CancellationToken ct)
    {
        var result = new Dictionary<string, TransportOrder>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var primary = await db.TransportOrders.AsNoTracking()
                .Where(order => order.CollectionDate == date)
                .OrderBy(order => order.Reference)
                .Take(2000)
                .ToListAsync(ct);
            foreach (var order in primary) result[order.Reference] = order;
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
        }

        var registered = await PlanningRegisterStore.ReadOrdersAsync(db, date, date, ct);
        foreach (var order in registered)
            if (!result.ContainsKey(order.Reference)) result[order.Reference] = order;
        return result.Values.OrderBy(order => order.Reference, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<Load>> ReadLoadsAsync(TmsDbContext db, DateOnly date, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Load>();
        try
        {
            var primary = await db.Loads.AsNoTracking().Include(load => load.Stops)
                .Where(load => load.PlanningDate == date)
                .OrderBy(load => load.Reference)
                .Take(1000)
                .ToListAsync(ct);
            foreach (var load in primary) result[load.Id] = load;
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
        }

        var registered = await PlanningRegisterStore.ReadLoadsAsync(db, date, ct);
        foreach (var load in registered) result[load.Id] = load;
        return result.Values.OrderBy(load => load.Reference, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }
}
