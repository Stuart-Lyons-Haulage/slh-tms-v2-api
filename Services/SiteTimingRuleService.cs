using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record SiteTimingRule(
    string RouteCombination,
    string? PalletType,
    string? LastDespatch,
    string? CollectFrom,
    string? CollectTo,
    string? DepotDeadline);

public sealed record SiteTimingWindow(DateTimeOffset? Start, DateTimeOffset? End);

public static class SiteTimingRuleMatcher
{
    public static SiteTimingRule? MatchForLoad(Load load, LoadStop deliveryStop, IReadOnlyList<SiteTimingRule> rules, IReadOnlyList<Site> sites)
    {
        var collection = load.Stops.OrderBy(stop => stop.Sequence)
            .FirstOrDefault(stop => stop.Name.StartsWith("Collect", StringComparison.OrdinalIgnoreCase));
        if (collection is null) return null;
        var collectionName = StripPrefix(collection.Name);
        var deliveryName = StripPrefix(deliveryStop.Name);
        return rules.FirstOrDefault(rule => Match(rule, collectionName, deliveryName, null, sites));
    }

    public static bool Match(SiteTimingRule rule, string? collectionName, string? deliveryName, string? palletType, IEnumerable<Site> sites)
    {
        var route = Normalize(rule.RouteCombination);
        if (string.IsNullOrWhiteSpace(route)) return false;
        if (!MatchesSite(route, collectionName, sites) || !MatchesSite(route, deliveryName, sites)) return false;
        return string.IsNullOrWhiteSpace(rule.PalletType) || string.IsNullOrWhiteSpace(palletType) ||
            Normalize(rule.PalletType) == Normalize(palletType) ||
            (Normalize(rule.PalletType) == "STD" && Normalize(palletType) is "STANDARD" or "STD");
    }

    public static SiteTimingWindow DeliveryWindow(SiteTimingRule rule, DateOnly deliveryDate)
    {
        var end = LocalTime(deliveryDate, rule.DepotDeadline);
        return new SiteTimingWindow(null, end);
    }

    public static SiteTimingWindow CollectionWindow(SiteTimingRule rule, DateOnly collectionDate)
    {
        var collectTo = LocalTime(collectionDate, rule.CollectTo);
        var lastDespatch = LocalTime(collectionDate, rule.LastDespatch);
        var end = collectTo is null ? lastDespatch : lastDespatch is null ? collectTo :
            collectTo.Value <= lastDespatch.Value ? collectTo : lastDespatch;
        return new SiteTimingWindow(LocalTime(collectionDate, rule.CollectFrom), end);
    }

    public static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string StripPrefix(string value)
    {
        var separator = value.IndexOf('·');
        return separator >= 0 ? value[(separator + 1)..].Trim() : value.Trim();
    }

    private static bool MatchesSite(string route, string? name, IEnumerable<Site> sites)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var site = sites.FirstOrDefault(item =>
            Normalize(item.Name) == Normalize(name) ||
            Normalize(item.DriverTextName) == Normalize(name) ||
            Normalize(item.ExternalCode) == Normalize(name) ||
            (!string.IsNullOrWhiteSpace(item.Aliases) && item.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(alias => Normalize(alias) == Normalize(name))));
        var keys = new[] { name, site?.ExternalCode, site?.Name, site?.DriverTextName, site?.Aliases }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(Normalize)
            .Where(value => value.Length >= 4);
        return keys.Any(key => route.Contains(key, StringComparison.OrdinalIgnoreCase) || key.Contains(route, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTimeOffset? LocalTime(DateOnly date, string? value)
    {
        if (!TimeOnly.TryParse(value, out var time)) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}

public sealed class SiteTimingRuleStore(TmsDbContext db, ILogger<SiteTimingRuleStore> logger)
{
    public async Task<IReadOnlyList<SiteTimingRule>> ReadAsync(CancellationToken ct)
    {
        try
        {
            var rows = await db.StagedImports.AsNoTracking()
                .Where(row => row.Status == StagingStatus.Promoted)
                .ToListAsync(ct);
            return rows.Where(row => row.EntityType.Equals("masterdetail:sitetimingrule", StringComparison.OrdinalIgnoreCase))
                .Select(Parse)
                .Where(rule => rule is not null)
                .Cast<SiteTimingRule>()
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Site timing master could not be read; timing constraints will be reported as unavailable.");
            return [];
        }
    }

    private static SiteTimingRule? Parse(StagedImport row)
    {
        try
        {
            using var document = JsonDocument.Parse(row.PayloadJson);
            var root = document.RootElement;
            string? Text(string name) => root.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value.ValueKind == JsonValueKind.Undefined
                ? null
                : root.EnumerateObject().First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value.ToString();
            var route = Text("routeCombination");
            return string.IsNullOrWhiteSpace(route) ? null : new SiteTimingRule(route, Text("palletType"), Text("lastDespatch"), Text("collectFrom"), Text("collectTo"), Text("depotDeadline"));
        }
        catch (JsonException) { return null; }
    }
}
