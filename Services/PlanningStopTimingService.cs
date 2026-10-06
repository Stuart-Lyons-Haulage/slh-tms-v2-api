using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Fills only missing planned stop times from preserved order evidence or an exact
/// Site Master route rule. Planner-entered or previously calculated times always win.
/// </summary>
public sealed class PlanningStopTimingService(TmsDbContext db, SiteTimingRuleStore timingRuleStore)
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public async Task ApplyMissingTimesAsync(Load load, CancellationToken ct)
    {
        if (load.Stops.Count == 0) return;

        var orderIds = load.Stops.Where(stop => stop.OrderId is not null).Select(stop => stop.OrderId!.Value).Distinct().ToArray();
        var orders = orderIds.Length == 0
            ? []
            : await db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)).ToListAsync(ct);
        var orderById = orders.ToDictionary(order => order.Id);
        var sourceLines = await ReadSourceLinesAsync(orders, ct);
        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).ToListAsync(ct);
        try { await MasterDetailStore.EnrichSitesAsync(db, sites, ct); } catch { /* route names remain usable */ }
        var rules = await timingRuleStore.ReadAsync(ct);
        LoadStop? currentCollection = null;

        foreach (var stop in load.Stops.OrderBy(item => item.Sequence))
        {
            if (IsCollection(stop.Name))
            {
                currentCollection = stop;
                continue;
            }
            if (stop.OrderId is not Guid orderId || !orderById.TryGetValue(orderId, out var order)) continue;

            var sourceLine = sourceLines.GetValueOrDefault(orderId)?.FirstOrDefault(line =>
                SameSite(line.CollectionSite, currentCollection?.Name) && SameSite(line.DeliverySite, stop.Name));
            var rule = rules.FirstOrDefault(item => SiteTimingRuleMatcher.Match(
                item, CleanStopName(currentCollection?.Name), CleanStopName(stop.Name), sourceLine?.PalletType, sites));

            if (currentCollection is not null && currentCollection.PlannedArrivalUtc is null)
            {
                var collectionTime = sourceLine?.CollectionTimeFrom;
                if (collectionTime is null && rule is not null)
                    collectionTime = TimeOnlyFrom(SiteTimingRuleMatcher.CollectionWindow(rule, load.PlanningDate).Start);
                if (collectionTime is not null)
                    currentCollection.PlannedArrivalUtc = Utc(load.PlanningDate, collectionTime.Value);
            }

            if (stop.PlannedArrivalUtc is not null) continue;
            var orderTime = order.DeliveryWindowStartUtc ?? order.DeliveryWindowEndUtc;
            if (orderTime is not null)
            {
                stop.PlannedArrivalUtc = orderTime;
                continue;
            }
            var masterDelivery = rule is null ? null : SiteTimingRuleMatcher.DeliveryWindow(rule, order.DeliveryDate ?? load.PlanningDate).End;
            if (masterDelivery is not null) stop.PlannedArrivalUtc = masterDelivery;
        }
    }

    private async Task<Dictionary<Guid, List<OrderSourceLine>>> ReadSourceLinesAsync(IReadOnlyCollection<TransportOrder> orders, CancellationToken ct)
    {
        var movementIds = orders.Where(order => order.SourceMovementId is not null).Select(order => order.SourceMovementId!.Value).ToArray();
        if (movementIds.Length == 0) return [];
        try
        {
            var movements = await db.OrderMovements.AsNoTracking().Where(item => movementIds.Contains(item.Id) && item.CurrentRevisionId != null).ToListAsync(ct);
            var revisionToOrder = movements.ToDictionary(item => item.CurrentRevisionId!.Value, item => orders.Single(order => order.SourceMovementId == item.Id).Id);
            if (revisionToOrder.Count == 0) return [];
            var lines = await db.OrderSourceLines.AsNoTracking().Where(line => revisionToOrder.Keys.Contains(line.RevisionId)).ToListAsync(ct);
            return lines.GroupBy(line => revisionToOrder[line.RevisionId]).ToDictionary(group => group.Key, group => group.ToList());
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private static bool IsCollection(string? name) => name?.StartsWith("Collect", StringComparison.OrdinalIgnoreCase) == true;
    private static string CleanStopName(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        var separator = text.IndexOf('·');
        return separator >= 0 ? text[(separator + 1)..].Trim() : text;
    }
    private static bool SameSite(string? left, string? right)
    {
        var leftKey = SiteTimingRuleMatcher.Normalize(CleanStopName(left));
        var rightKey = SiteTimingRuleMatcher.Normalize(CleanStopName(right));
        return leftKey.Length > 0 && rightKey.Length > 0 && (leftKey == rightKey || leftKey.Contains(rightKey, StringComparison.Ordinal) || rightKey.Contains(leftKey, StringComparison.Ordinal));
    }
    private static TimeOnly? TimeOnlyFrom(DateTimeOffset? value) => value is null ? null : TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.Value, London).DateTime);
    private static DateTimeOffset Utc(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, London), TimeSpan.Zero);
    }
}
