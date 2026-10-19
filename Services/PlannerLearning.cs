using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Learns from what planners actually do. Every time an order is first put on a run, the
/// decision (customer, destination, which run, how many pallets, which weekday) is recorded.
/// From the recent history the TMS works out each customer/destination's usual run and usual
/// size, how consistently that pattern is followed, and surfaces it back as a suggestion when
/// the same work comes round again. Nothing is applied automatically.
/// </summary>
public static class PlannerLearning
{
    public const string DecisionType = "plannerdecision";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Decision(
        DateOnly Date, Guid OrderId, string Reference, string? CustomerCode, string Collection, string Destination,
        string? PlanningGroup, Guid LoadId, string RunReference, string RunPattern, int Pallets, string Weekday,
        DateTimeOffset AtUtc, string? Actor);

    public sealed record Preference(
        string Customer, string Destination, string PreferredRun, string Pattern, int Confidence, int Samples,
        int TypicalPallets, IReadOnlyList<string> Weekdays);

    public sealed record Insights(
        DateTimeOffset GeneratedAtUtc, int Days, int Decisions, int Customers, int Consistency,
        IReadOnlyList<Preference> Preferences);

    public sealed record OrderHint(Guid OrderId, string Message, string Pattern, int Confidence, Guid? MatchingLoadId, string? MatchingRun, int? TypicalPallets, string? PalletWarning);

    /// <summary>Record one "order put on a run" decision. Called after an allocation is saved.</summary>
    public static async Task RecordAsync(TmsDbContext db, Decision decision, CancellationToken ct)
    {
        var key = $"plannerdecision:{decision.OrderId:N}:{decision.LoadId:N}:{decision.AtUtc:yyyyMMddHHmmssfff}";
        if (key.Length > 200) key = key[..200];
        db.StagedImports.Add(new StagedImport
        {
            EntityType = DecisionType,
            IdempotencyKey = key,
            PayloadJson = JsonSerializer.Serialize(decision, Json),
            Status = StagingStatus.Promoted,
            Source = "Planner learning",
            ReceivedAtUtc = decision.AtUtc
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Normalise a run reference to a repeatable pattern: drop the date, keep "RUN 4 PM".</summary>
    public static string RunPattern(string? reference)
    {
        var value = (reference ?? string.Empty).ToUpperInvariant();
        value = Regex.Replace(value, @"\b(?:MON|TUE|WED|THU|FRI|SAT|SUN)[A-Z]*\b", " ");
        value = Regex.Replace(value, @"\b\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?\b", " "); // dates
        value = Regex.Replace(value, @"\b\d{1,2}[:.]\d{2}\b", " ");                      // times
        value = Regex.Replace(value, @"\b\d{5,}\b", " ");                                // long ids
        value = Regex.Replace(value, @"\b(19|20)\d{2}\b", " ");                          // years
        value = Regex.Replace(value, @"[^A-Z0-9]+", " ").Trim();
        return value.Length == 0 ? (reference ?? string.Empty).Trim().ToUpperInvariant() : value;
    }

    private static string Key(string? customer, string destination) =>
        $"{Norm(customer)}|{Norm(destination)}";

    public static async Task<List<Decision>> ReadRecentAsync(TmsDbContext db, int days, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 7, 365));
        var rows = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == DecisionType && item.ReceivedAtUtc >= since)
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(20000)
            .Select(item => item.PayloadJson)
            .ToListAsync(ct);
        var result = new List<Decision>();
        foreach (var json in rows)
        {
            try { if (JsonSerializer.Deserialize<Decision>(json, Json) is { } decision) result.Add(decision); }
            catch (JsonException) { }
        }
        return result;
    }

    public static Insights Learn(IReadOnlyList<Decision> decisions, int days, DateTimeOffset now)
    {
        var groups = decisions.GroupBy(decision => Key(decision.CustomerCode, decision.Destination)).ToList();
        var preferences = new List<Preference>();
        var followed = 0;
        var counted = 0;
        foreach (var group in groups)
        {
            var items = group.ToList();
            var byPattern = items.GroupBy(item => item.RunPattern).OrderByDescending(set => set.Count()).ToList();
            var top = byPattern[0];
            var share = (int)Math.Round(100.0 * top.Count() / items.Count);
            // Consistency across the whole window: did each decision match its group's usual run?
            counted += items.Count;
            followed += top.Count();
            if (items.Count < 3 || share < 60) continue;
            var sample = top.OrderByDescending(item => item.AtUtc).First();
            preferences.Add(new Preference(
                sample.CustomerCode ?? "Unknown", sample.Destination, sample.RunReference, top.Key, share, items.Count,
                TypicalPallets(items), items.Select(item => item.Weekday).Where(day => !string.IsNullOrWhiteSpace(day)).Distinct().ToList()));
        }
        var consistency = counted == 0 ? 0 : (int)Math.Round(100.0 * followed / counted);
        return new Insights(now, days, decisions.Count,
            groups.Count, consistency,
            preferences.OrderByDescending(preference => preference.Samples).ThenByDescending(preference => preference.Confidence).ToList());
    }

    private static int TypicalPallets(List<Decision> items)
    {
        var values = items.Select(item => item.Pallets).Where(value => value > 0).OrderBy(value => value).ToList();
        if (values.Count == 0) return 0;
        return values[values.Count / 2]; // median
    }

    /// <summary>For the orders on a day, the learned preferred run (and a size warning) where known.</summary>
    public static List<OrderHint> HintsFor(
        IReadOnlyList<Decision> history,
        IEnumerable<(Guid OrderId, string? CustomerCode, string Destination, int Pallets)> orders,
        IReadOnlyList<(Guid LoadId, string Reference)> todaysRuns,
        DateTimeOffset now)
    {
        var insights = Learn(history, 90, now);
        var byKey = insights.Preferences.ToDictionary(preference => Key(preference.Customer, preference.Destination), preference => preference);
        var hints = new List<OrderHint>();
        foreach (var order in orders)
        {
            if (!byKey.TryGetValue(Key(order.CustomerCode, order.Destination), out var preference)) continue;
            var match = todaysRuns.FirstOrDefault(run => RunPattern(run.Reference) == preference.Pattern);
            string? warning = null;
            if (preference.TypicalPallets > 0 && order.Pallets > 0 &&
                (order.Pallets >= preference.TypicalPallets * 3 || order.Pallets * 3 <= preference.TypicalPallets))
                warning = $"Usually about {preference.TypicalPallets} pallets for {preference.Customer} → {preference.Destination}; this is {order.Pallets}. Worth a check.";
            var runText = match.Reference is not null ? $"{match.Reference}" : $"a \"{preference.PreferredRun}\" run";
            hints.Add(new OrderHint(order.OrderId,
                $"Usually goes on {runText} ({preference.Confidence}% of {preference.Samples})",
                preference.Pattern, preference.Confidence, match.LoadId == Guid.Empty ? null : match.LoadId, match.Reference,
                preference.TypicalPallets > 0 ? preference.TypicalPallets : null, warning));
        }
        return hints;
    }

    public static string Weekday(DateOnly date) => date.DayOfWeek.ToString()[..3];
    private static string Norm(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
