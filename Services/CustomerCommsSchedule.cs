using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// When customer emails go out on their own: load plans for tomorrow in the late afternoon
/// once the plan is built, delivery times for today mid-morning. Each customer is switched
/// on separately; nothing is sent for a customer until a planner turns it on.
/// </summary>
public static class CustomerCommsSchedule
{
    public const string SettingsKey = "setting:customer-comms-schedule";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, string> Holds = new(StringComparer.OrdinalIgnoreCase);

    public sealed record Choice(bool LoadPlan, bool DeliveryTimes);

    public sealed record Settings(string LoadPlanTime, string DeliveryTimesTime, string LoadPlanLatest, Dictionary<string, Choice> Customers);

    public static Settings Default() => new("16:30", "10:30", "19:00", new Dictionary<string, Choice>(StringComparer.OrdinalIgnoreCase));

    public static bool IsTime(string? value) => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static TimeOnly Time(string value, TimeOnly fallback) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : fallback;

    public static async Task<Settings> LoadAsync(TmsDbContext db, CancellationToken ct)
    {
        var json = await db.StagedImports.AsNoTracking().Where(item => item.IdempotencyKey == SettingsKey).Select(item => item.PayloadJson).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return Default();
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(json, Json);
            if (settings is null) return Default();
            return settings with { Customers = new Dictionary<string, Choice>(settings.Customers ?? [], StringComparer.OrdinalIgnoreCase) };
        }
        catch (JsonException) { return Default(); }
    }

    public static async Task SaveAsync(TmsDbContext db, Settings settings, string? user, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(settings, Json);
        var row = await db.StagedImports.SingleOrDefaultAsync(item => item.IdempotencyKey == SettingsKey, ct);
        if (row is null)
        {
            row = new StagedImport { EntityType = "setting", IdempotencyKey = SettingsKey, PayloadJson = payload, Status = StagingStatus.Promoted, Source = "Customer Comms" };
            db.StagedImports.Add(row);
        }
        else row.PayloadJson = payload;
        row.ReviewedAtUtc = DateTimeOffset.UtcNow;
        row.ReviewedBy = user;
        db.StagedImportEvents.Add(StagingAudit.Create(row, "CustomerCommsScheduleChanged", row.Status, payload.Length > 400 ? payload[..400] : payload, user));
        await db.SaveChangesAsync(ct);
    }

    public static void RecordHold(string customerCode, DateOnly date, string? reason)
    {
        var key = $"{customerCode}|{date:yyyy-MM-dd}";
        if (reason is null) Holds.TryRemove(key, out _); else Holds[key] = reason;
    }

    public static string? HoldFor(string customerCode, DateOnly date) => Holds.GetValueOrDefault($"{customerCode}|{date:yyyy-MM-dd}");
}

/// <summary>Checks every five minutes whether a scheduled customer email is due.</summary>
public sealed class CustomerCommsScheduler(IServiceScopeFactory scopes, ILogger<CustomerCommsScheduler> logger) : BackgroundService
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Scheduled customer email check failed; it will try again in five minutes.");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var settings = await CustomerCommsSchedule.LoadAsync(db, ct);
        if (!settings.Customers.Values.Any(choice => choice.LoadPlan || choice.DeliveryTimes)) return;

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London);
        var time = TimeOnly.FromDateTime(now.DateTime);
        var today = DateOnly.FromDateTime(now.DateTime);
        var loadPlanFrom = CustomerCommsSchedule.Time(settings.LoadPlanTime, new TimeOnly(16, 30));
        var loadPlanUntil = CustomerCommsSchedule.Time(settings.LoadPlanLatest, new TimeOnly(19, 0));
        var timesFrom = CustomerCommsSchedule.Time(settings.DeliveryTimesTime, new TimeOnly(10, 30));

        var controller = new CustomerLoadPlansController(db, scope.ServiceProvider.GetRequiredService<ILogger<CustomerLoadPlansController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Scheduled send")], "schedule")) }
            }
        };

        foreach (var (customer, choice) in settings.Customers)
        {
            if (choice.LoadPlan && time >= loadPlanFrom && time <= loadPlanUntil)
            {
                var date = today.AddDays(1);
                var outcome = await controller.QueueAutomaticAsync(date, customer, deliveryTimes: false, ct);
                CustomerCommsSchedule.RecordHold(customer, date, outcome.StartsWith("Held", StringComparison.Ordinal) ? outcome : null);
                if (outcome == "Queued") logger.LogInformation("Scheduled load plan queued for {Customer} {Date}.", customer, date);
                else if (outcome.StartsWith("Held", StringComparison.Ordinal) && time >= loadPlanUntil.AddMinutes(-5))
                    logger.LogWarning("Scheduled load plan for {Customer} {Date} was not sent: {Reason}.", customer, date, outcome);
            }
            // Delivery times go out once from the set time until early afternoon.
            if (choice.DeliveryTimes && time >= timesFrom && time <= new TimeOnly(14, 0))
            {
                var outcome = await controller.QueueAutomaticAsync(today, customer, deliveryTimes: true, ct);
                if (outcome == "Queued") logger.LogInformation("Scheduled delivery times queued for {Customer} {Date}.", customer, today);
            }
        }
    }
}
