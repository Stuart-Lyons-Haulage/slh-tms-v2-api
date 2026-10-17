using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/assistant"), Authorize]
public sealed class PlannerEfficiencyController(TmsDbContext db, ILogger<PlannerEfficiencyController> logger) : ControllerBase
{
    [HttpGet("efficiency")]
    public async Task<IActionResult> Efficiency([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var previousDate = planningDate.AddDays(-1);

        var drivers = await SafeRead(db.Drivers.AsNoTracking().Where(x => x.Active), "drivers", ct);
        var sites = await SafeRead(db.Sites.AsNoTracking().Where(x => x.Active), "sites", ct);
        if (sites.Count > 0)
        {
            try { await MasterDetailStore.EnrichSitesAsync(db, sites, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Assistant efficiency site enrichment was unavailable; base site records will be used.");
            }
        }

        var previousLoads = await SafeLoads(previousDate, ct);
        var todayLoads = await SafeLoads(planningDate, ct);
        var orders = await SafeOrders(planningDate, ct);

        var plannedIds = todayLoads.SelectMany(x => x.Stops ?? [])
            .Where(x => x.OrderId is not null)
            .Select(x => x.OrderId!.Value)
            .ToHashSet();

        var openOrders = orders
            .Where(x => !plannedIds.Contains(x.Id) && x.Status is not OrderStatus.Cancelled and not OrderStatus.Delivered)
            .ToList();

        var candidates = openOrders.Select(order =>
        {
            var collectionName = order.SellerName ?? Tagged(order.DriverInstructions, "Collection site");
            var destination = order.StallNumber ?? Tagged(order.DriverInstructions, "Depot") ?? order.MarketName ?? order.Reference;
            var site = FindSite(sites, collectionName);
            return new JobCandidate(order, collectionName, destination, site?.Latitude, site?.Longitude);
        }).ToList();

        var driverEnds = previousLoads
            .Where(load => load.DriverId is not null)
            .Select(load =>
            {
                var stops = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
                var last = stops.LastOrDefault();
                var driver = drivers.FirstOrDefault(x => x.Id == load.DriverId);
                return new DriverEnd(
                    load.DriverId!.Value,
                    driver?.DisplayName ?? "Unknown driver",
                    load.Reference,
                    last?.Name,
                    last?.Address,
                    last?.Latitude,
                    last?.Longitude);
            })
            .GroupBy(x => x.DriverId)
            .Select(group => group.Last())
            .ToList();

        var usedOrders = new HashSet<Guid>();
        var suggestions = new List<object>();

        foreach (var end in driverEnds)
        {
            var ranked = candidates
                .Where(job => !usedOrders.Contains(job.Order.Id))
                .Select(job => new
                {
                    Job = job,
                    Miles = DistanceMiles(end.Latitude, end.Longitude, job.Latitude, job.Longitude),
                    TextMatch = TextAffinity(end.LastStopName, end.LastStopAddress, job.CollectionName)
                })
                .Where(x => x.Miles is not null || x.TextMatch)
                .OrderBy(x => x.Miles ?? (x.TextMatch ? 0m : 9999m))
                .ThenBy(x => x.Job.Order.Reference)
                .FirstOrDefault();

            if (ranked is null) continue;
            usedOrders.Add(ranked.Job.Order.Id);
            suggestions.Add(new
            {
                driverId = end.DriverId,
                driverName = end.DriverName,
                previousRun = end.PreviousRun,
                previousEnd = end.LastStopName ?? end.LastStopAddress ?? "Previous final stop not mapped",
                orderId = ranked.Job.Order.Id,
                orderReference = ranked.Job.Order.Reference,
                collection = ranked.Job.CollectionName ?? "Collection not mapped",
                destination = ranked.Job.Destination,
                estimatedRepositionMiles = ranked.Miles is null ? (decimal?)null : Math.Round(ranked.Miles.Value, 1),
                reason = ranked.Miles is not null
                    ? $"Starts about {Math.Round(ranked.Miles.Value, 1)} miles from the driver's previous final mapped stop."
                    : "Collection text matches the driver's previous final stop."
            });
        }

        return Ok(new
        {
            planningDate,
            previousDate,
            previousDayAllocatedDrivers = driverEnds.Count,
            unplannedOrders = openOrders.Count,
            suggestedContinuations = suggestions.Count,
            suggestions,
            message = driverEnds.Count == 0
                ? "No previous-day allocated driver runs were available to compare."
                : suggestions.Count == 0
                    ? "Previous-day runs were found, but there are not enough mapped collection points to make reliable continuity suggestions."
                    : $"Built {suggestions.Count} previous-day continuity suggestion(s) using final stop and next collection proximity."
        });
    }

    private async Task<List<T>> SafeRead<T>(IQueryable<T> query, string area, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant efficiency skipped unavailable {Area} data.", area);
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private async Task<List<Load>> SafeLoads(DateOnly date, CancellationToken ct)
    {
        try
        {
            var loads = await db.Loads.AsNoTracking().Include(x => x.Stops)
                .Where(x => x.PlanningDate == date && x.Status != LoadStatus.Cancelled)
                .OrderBy(x => x.Reference).ToListAsync(ct);
            if (loads.Count > 0) return loads;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant efficiency primary load table unavailable; using planning register.");
            db.ChangeTracker.Clear();
        }
        try { return await PlanningRegisterStore.ReadLoadsAsync(db, date, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant efficiency planning-register load fallback unavailable.");
            return [];
        }
    }

    private async Task<List<TransportOrder>> SafeOrders(DateOnly date, CancellationToken ct)
    {
        try
        {
            var orders = await db.TransportOrders.AsNoTracking()
                .Where(x => x.CollectionDate == date && x.Status != OrderStatus.Cancelled)
                .OrderBy(x => x.Reference).ToListAsync(ct);
            if (orders.Count > 0) return orders;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant efficiency primary order table unavailable; using planning register.");
            db.ChangeTracker.Clear();
        }
        try { return await PlanningRegisterStore.ReadOrdersAsync(db, date, date, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant efficiency planning-register order fallback unavailable.");
            return [];
        }
    }

    private static Site? FindSite(IEnumerable<Site> sites, string? value)
    {
        var key = Normalise(value);
        if (key.Length == 0) return null;
        return sites.FirstOrDefault(site => new[] { site.ExternalCode, site.Name, site.DriverTextName }
            .Concat((site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(candidate => Normalise(candidate) == key));
    }

    private static bool TextAffinity(string? previousName, string? previousAddress, string? collection)
    {
        var target = Normalise(collection);
        if (target.Length < 3) return false;
        return Normalise(previousName).Contains(target, StringComparison.OrdinalIgnoreCase)
            || Normalise(previousAddress).Contains(target, StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? DistanceMiles(decimal? lat1, decimal? lon1, decimal? lat2, decimal? lon2)
    {
        if (lat1 is null || lon1 is null || lat2 is null || lon2 is null) return null;
        const double earthMiles = 3958.7613;
        static double Rad(decimal value) => (double)value * Math.PI / 180d;
        var a1 = Rad(lat1.Value); var a2 = Rad(lat2.Value);
        var dLat = a2 - a1; var dLon = Rad(lon2.Value - lon1.Value);
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(a1) * Math.Cos(a2) * Math.Pow(Math.Sin(dLon / 2), 2);
        return (decimal)(earthMiles * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)));
    }

    private static string? Tagged(string? notes, string label)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var prefix = $"{label}:";
        return notes.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record JobCandidate(TransportOrder Order, string? CollectionName, string Destination, decimal? Latitude, decimal? Longitude);
    private sealed record DriverEnd(Guid DriverId, string DriverName, string PreviousRun, string? LastStopName, string? LastStopAddress, decimal? Latitude, decimal? Longitude);
}
