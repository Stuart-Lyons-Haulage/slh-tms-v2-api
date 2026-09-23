using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record MorningBriefingPreviewItem(
    Guid CustomerId,
    string CustomerCode,
    string CustomerName,
    string OrderReference,
    Guid StopId,
    string DeliverySite,
    DateTimeOffset? EtaUtc,
    string EtaWindow,
    IReadOnlyList<string> EmailRecipients,
    IReadOnlyList<string> SmsRecipients);

public sealed record MorningBriefingPreview(
    DateOnly PlanningDate,
    DateTimeOffset GeneratedAtUtc,
    bool LiveSendingEnabled,
    IReadOnlyList<MorningBriefingPreviewItem> Items);

public sealed class CustomerNotificationService(
    TmsDbContext db,
    TelemetryClient telemetry,
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    private readonly TimeZoneInfo _ukZone = ResolveUkZone();

    public async Task<MorningBriefingPreview> PreviewMorningBriefingAsync(DateOnly planningDate, CancellationToken ct)
    {
        var loads = await PlanningResilience.ReadLoadsAsync(db, planningDate, ct);
        var deliveryStops = loads
            .Where(load => load.Status != LoadStatus.Cancelled)
            .SelectMany(load => load.Stops)
            .Where(stop => stop.OrderId != null)
            .GroupBy(stop => stop.OrderId!.Value)
            .Select(group => group.OrderByDescending(stop => stop.Sequence).First())
            .ToList();
        var orderIds = deliveryStops.Select(stop => stop.OrderId!.Value).Distinct().ToList();
        if (orderIds.Count == 0)
            return new MorningBriefingPreview(planningDate, timeProvider.GetUtcNow(), configuration.GetValue("CustomerNotifications:Enabled", false), []);

        var orders = await db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)).ToDictionaryAsync(order => order.Id, ct);
        var customerCodes = orders.Values.Select(order => order.CustomerCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var customers = await db.Customers.AsNoTracking().Where(customer => customerCodes.Contains(customer.Code)).ToListAsync(ct);
        var customersByCode = customers.GroupBy(customer => customer.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var contacts = await db.CustomerContacts.AsNoTracking()
            .Where(contact => contact.Active && contact.ReceivesEtaUpdates && customerCodes.Contains(contact.CustomerCode))
            .ToListAsync(ct);
        var contactsByCode = contacts.GroupBy(contact => contact.CustomerCode, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var stopIds = deliveryStops.Select(stop => stop.Id).ToList();
        var snapshots = await db.EtaSnapshots.AsNoTracking()
            .Where(snapshot => stopIds.Contains(snapshot.StopId) && snapshot.EtaUtc != null)
            .OrderByDescending(snapshot => snapshot.CapturedAtUtc)
            .ToListAsync(ct);
        var latestByStop = snapshots.GroupBy(snapshot => snapshot.StopId).ToDictionary(group => group.Key, group => group.First());

        var result = new List<MorningBriefingPreviewItem>();
        foreach (var stop in deliveryStops.OrderBy(stop => stop.PlannedArrivalUtc))
        {
            if (!orders.TryGetValue(stop.OrderId!.Value, out var order)) continue;
            if (!customersByCode.TryGetValue(order.CustomerCode, out var customer)) continue;
            contactsByCode.TryGetValue(order.CustomerCode, out var customerContacts);
            customerContacts ??= [];
            latestByStop.TryGetValue(stop.Id, out var etaSnapshot);
            var eta = etaSnapshot?.EtaUtc ?? stop.PlannedArrivalUtc;
            var window = FormatEtaWindow(eta);
            result.Add(new MorningBriefingPreviewItem(
                customer.Id,
                customer.Code,
                customer.Name,
                order.Reference,
                stop.Id,
                stop.Name,
                eta,
                window,
                customerContacts.Where(contact => !string.IsNullOrWhiteSpace(contact.Email)).Select(contact => contact.Email!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                customerContacts.Where(contact => !string.IsNullOrWhiteSpace(contact.MobileNumber)).Select(contact => contact.MobileNumber!).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return new MorningBriefingPreview(
            planningDate,
            timeProvider.GetUtcNow(),
            configuration.GetValue("CustomerNotifications:Enabled", false),
            result);
    }

    public async Task RecordSentAsync(CustomerNotificationLog log, CancellationToken ct)
    {
        await CustomerNotificationStore.InsertAsync(db, log, ct);
        telemetry.TrackEvent("CustomerNotificationSent", new Dictionary<string, string>
        {
            ["customerId"] = log.CustomerId.ToString(),
            ["orderReference"] = log.OrderReference,
            ["type"] = log.Type.ToString(),
            ["channel"] = log.Channel.ToString(),
            ["deliveryConfirmed"] = log.DeliveryConfirmed.ToString()
        });
    }

    public Task<List<CustomerNotificationLog>> ReadLogAsync(Guid customerId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var fromUtc = LocalDateStartUtc(from);
        var toUtc = LocalDateStartUtc(to.AddDays(1));
        return CustomerNotificationStore.ListAsync(db, customerId, fromUtc, toUtc, ct);
    }

    private string FormatEtaWindow(DateTimeOffset? eta)
    {
        if (eta is null) return "ETA unavailable";
        var local = TimeZoneInfo.ConvertTime(eta.Value, _ukZone);
        return $"{local.AddMinutes(-15):HH:mm}–{local.AddMinutes(15):HH:mm}";
    }

    private DateTimeOffset LocalDateStartUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, _ukZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static TimeZoneInfo ResolveUkZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }
}
