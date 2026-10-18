using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Follows an amendment or cancellation through to dispatch. When a live order changes,
/// it works out how far that order has got and raises one "change to action" record:
///   - not on a run yet: applied, nothing more to do (recorded for the audit trail only);
///   - on a planned run: the run needs updating before it is sent;
///   - on a run already sent to Samsara: the driver's live route must be amended.
/// Open records stay on Dispatch / Order Control until a planner marks them done.
/// </summary>
public static class OrderChangeImpact
{
    public const string EntityType = "orderchange";

    public sealed record FieldChange(string Field, string? From, string? To);
    public sealed record RunImpact(Guid LoadId, string Reference, string Status, string? SamsaraRouteId, bool Live);

    public static OrderSnapshot Snapshot(TransportOrder order) => new(
        order.Reference, order.CollectionDate, order.DeliveryDate, order.Pallets, order.SellerName, order.StallNumber,
        order.DeliveryWindowStartUtc, order.Status);

    public sealed record OrderSnapshot(string Reference, DateOnly CollectionDate, DateOnly? DeliveryDate, int? Pallets,
        string? Collection, string? Destination, DateTimeOffset? DeliveryWindowStartUtc, OrderStatus Status);

    public static List<FieldChange> Diff(OrderSnapshot before, OrderSnapshot after)
    {
        var changes = new List<FieldChange>();
        void Add(string field, object? from, object? to)
        {
            var a = Format(from);
            var b = Format(to);
            if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) changes.Add(new FieldChange(field, a, b));
        }
        Add("Collection date", before.CollectionDate, after.CollectionDate);
        Add("Delivery date", before.DeliveryDate, after.DeliveryDate);
        Add("Quantity", before.Pallets, after.Pallets);
        Add("Collection", before.Collection, after.Collection);
        Add("Delivery point", before.Destination, after.Destination);
        Add("Delivery time", before.DeliveryWindowStartUtc, after.DeliveryWindowStartUtc);
        if (before.Status != after.Status && after.Status == OrderStatus.Cancelled) changes.Add(new FieldChange("Status", before.Status.ToString(), "Cancelled"));
        return changes;
    }

    /// <summary>Record the change and where it has to be actioned. Returns null when nothing
    /// operational changed.</summary>
    public static async Task<StagedImport?> RecordAsync(
        TmsDbContext db, TransportOrder order, string kind, IReadOnlyList<FieldChange> changes,
        string? source, string? actor, CancellationToken ct, IReadOnlyList<RunImpact>? knownRuns = null)
    {
        if (changes.Count == 0) return null;
        var runs = knownRuns ?? await RunsForOrderAsync(db, order, ct);
        var live = runs.Any(run => run.Live);
        var stage = live ? "LiveRoute" : runs.Count > 0 ? "OnRun" : "NotPlanned";
        var action = stage switch
        {
            "LiveRoute" => kind == "Cancelled"
                ? "Cancelled after dispatch: remove the stop from the driver's Samsara route and tell the driver."
                : "Changed after dispatch: amend the driver's Samsara route and tell the driver.",
            "OnRun" => kind == "Cancelled"
                ? "Cancelled: it has been taken off the run; check the run before it is sent."
                : "Changed while on a run: check the run (space, timings) before it is sent.",
            _ => "Applied to the order; not on a run yet, nothing else to do."
        };

        var payload = JsonSerializer.Serialize(new
        {
            orderId = order.Id,
            reference = order.Reference,
            customerCode = order.CustomerCode,
            collectionDate = order.CollectionDate.ToString("yyyy-MM-dd"),
            kind,
            stage,
            action,
            changes,
            runs,
            source,
            raisedBy = actor,
            raisedAtUtc = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var record = new StagedImport
        {
            EntityType = EntityType,
            IdempotencyKey = $"orderchange:{order.Id:N}:{Guid.NewGuid():N}",
            PayloadJson = payload,
            // Changes that need nobody to do anything are closed at once but kept for audit.
            Status = stage == "NotPlanned" ? StagingStatus.Approved : StagingStatus.PendingReview,
            Source = source is null ? kind : $"{kind} / {source}"[..Math.Min(200, $"{kind} / {source}".Length)],
            ReviewedBy = stage == "NotPlanned" ? "Applied automatically" : null,
            ReviewedAtUtc = stage == "NotPlanned" ? DateTimeOffset.UtcNow : null
        };
        db.StagedImports.Add(record);
        db.StagedImportEvents.Add(StagingAudit.Create(record, "ChangeRaised", null, action, actor ?? "Order change"));
        return record;
    }

    public static async Task<List<RunImpact>> RunsForOrderAsync(TmsDbContext db, TransportOrder order, CancellationToken ct)
    {
        var loads = new List<Load>();
        foreach (var date in new[] { order.CollectionDate.AddDays(-1), order.CollectionDate, order.CollectionDate.AddDays(1) })
        {
            try { loads.AddRange(await PlanningResilience.ReadLoadsAsync(db, date, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        var onRuns = loads
            .Where(load => load.Status != LoadStatus.Cancelled && load.Status != LoadStatus.Completed && load.Stops.Any(stop => stop.OrderId == order.Id))
            .GroupBy(load => load.Id)
            .Select(group => group.First())
            .ToList();
        if (onRuns.Count == 0) return [];

        var loadIds = onRuns.Select(load => load.Id).ToList();
        var routes = await db.IntegrationMappings.AsNoTracking()
            .Where(item => item.Provider == "Samsara" && item.Active &&
                           (item.TmsEntityType == "Load" || item.TmsEntityType == "LoadRelay") &&
                           loadIds.Contains(item.TmsEntityId))
            .ToListAsync(ct);
        return onRuns.Select(load =>
        {
            var route = routes.FirstOrDefault(item => item.TmsEntityId == load.Id);
            var live = route is not null || load.Status is LoadStatus.Dispatched or LoadStatus.InProgress;
            return new RunImpact(load.Id, load.Reference, load.Status.ToString(), route?.ExternalKey, live);
        }).ToList();
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        DateOnly date => date.ToString("ddd dd/MM"),
        DateTimeOffset time => TimeZoneInfo.ConvertTime(time, London()).ToString("ddd dd/MM HH:mm"),
        _ => value.ToString()
    };

    private static TimeZoneInfo London()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }
}
