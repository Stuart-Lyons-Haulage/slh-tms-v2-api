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
    string? DepotDeadline,
    string? CollectionKey = null,
    string? DeliveryKey = null,
    string? CollectionContext = null);

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
        if (!MatchesCollection(rule, collectionName, sites) || !MatchesDelivery(rule, deliveryName, sites)) return false;
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

    // The timing seed stores explicit route-end keys. Match these before Site Master
    // aliases: NWF collection sites may not yet have Site rows in production.
    private static bool MatchesCollection(SiteTimingRule rule, string? name, IEnumerable<Site> sites)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (string.IsNullOrWhiteSpace(rule.CollectionKey))
            return MatchesSite(Normalize(rule.RouteCombination), name, sites);

        var key = Normalize(rule.CollectionKey);
        var actual = Normalize(name);
        if (actual == key && string.IsNullOrWhiteSpace(rule.CollectionContext)) return true;

        // NWFSELSEY and NWFDRAYTON must not match each other's rules.
        var context = Normalize(rule.CollectionContext);
        var location = context switch
        {
            "SELSEY" => "SEL",
            "DRAYTON" => "DRA",
            "MERSTON" => "MER",
            _ => context
        };
        var route = Normalize(rule.RouteCombination);
        if (location.Length > 0 && !route.StartsWith(key + location, StringComparison.Ordinal)) return false;
        if (actual.StartsWith(key, StringComparison.Ordinal) &&
            (context.Length == 0 || actual.Contains(context, StringComparison.Ordinal) ||
             (location.Length >= 3 && actual.Contains(location, StringComparison.Ordinal)))) return true;
        return MatchesSite(route, name, sites) &&
            (context.Length == 0 || route.StartsWith(key + location, StringComparison.Ordinal));
    }

    private static bool MatchesDelivery(SiteTimingRule rule, string? name, IEnumerable<Site> sites)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (string.IsNullOrWhiteSpace(rule.DeliveryKey))
            return MatchesSite(Normalize(rule.RouteCombination), name, sites);
        var route = Normalize(rule.RouteCombination);
        var key = Normalize(rule.DeliveryKey);
        var actual = Normalize(name);
        // Exact destination suffix keeps Gadbrook and Gadbrook Produce distinct.
        return key.Length >= 4 && route.EndsWith(key, StringComparison.Ordinal) &&
            (actual == key || actual.EndsWith(key, StringComparison.Ordinal) ||
             sites.Any(site => Normalize(site.Name) == actual &&
                               (Normalize(site.ExternalCode) == key || Normalize(site.DriverTextName).EndsWith(key, StringComparison.Ordinal))));
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

public static class DispatchRouteTiming
{
    public static DateTimeOffset? PlannedCollectionTimeUtc(
        Load load,
        LoadStop collectionStop,
        IReadOnlyList<SiteTimingRule> rules,
        IReadOnlyList<Site> sites)
    {
        if (!collectionStop.Name.StartsWith("Collect", StringComparison.OrdinalIgnoreCase))
            return null;

        var finalDelivery = load.Stops.OrderBy(stop => stop.Sequence)
            .LastOrDefault(stop => !stop.Name.StartsWith("Collect", StringComparison.OrdinalIgnoreCase));
        var rule = finalDelivery is null
            ? null
            : SiteTimingRuleMatcher.MatchForLoad(load, finalDelivery, rules, sites);
        return rule is null
            ? collectionStop.PlannedArrivalUtc
            : SiteTimingRuleMatcher.CollectionWindow(rule, load.PlanningDate).Start ?? collectionStop.PlannedArrivalUtc;
    }

    public static DateTimeOffset? FinalDeliveryCutoffTimeUtc(
        Load load,
        LoadStop deliveryStop,
        IReadOnlyList<SiteTimingRule> rules,
        IReadOnlyList<Site> sites)
    {
        if (deliveryStop.Name.StartsWith("Collect", StringComparison.OrdinalIgnoreCase))
            return null;

        var finalDelivery = load.Stops.OrderBy(stop => stop.Sequence)
            .LastOrDefault(stop => !stop.Name.StartsWith("Collect", StringComparison.OrdinalIgnoreCase));
        if (finalDelivery?.Id != deliveryStop.Id)
            return null;

        var rule = SiteTimingRuleMatcher.MatchForLoad(load, deliveryStop, rules, sites);
        return rule is null
            ? null
            : SiteTimingRuleMatcher.DeliveryWindow(rule, load.PlanningDate).End;
    }
}

public sealed class SiteTimingRuleStore(TmsDbContext db, ILogger<SiteTimingRuleStore> logger)
{
    public async Task<IReadOnlyList<SiteTimingRule>> ReadAsync(CancellationToken ct)
    {
        try
        {
            // Filter by EntityType in SQL rather than loading every Promoted StagedImport row
            // (orders, email evidence, driver/tacho/sage syncs - everything) and filtering to
            // the handful of site-timing rows in memory. That full-table load is slow enough,
            // now that StagedImports has grown, to fail outright - and a failure here is
            // swallowed below and reported as "no rules", which silently guesses every route's
            // collection/delivery time instead of surfacing a fetch problem.
            var rows = await db.StagedImports.AsNoTracking()
                .Where(row => row.Status == StagingStatus.Promoted && row.EntityType == "masterdetail:sitetimingrule")
                .ToListAsync(ct);
            return rows.Select(Parse)
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
            return string.IsNullOrWhiteSpace(route) ? null : new SiteTimingRule(route, Text("palletType"), Text("lastDespatch"), Text("collectFrom"), Text("collectTo"), Text("depotDeadline"), Text("collectionKey"), Text("deliveryKey"), Text("collectionContext"));
        }
        catch (JsonException) { return null; }
    }
}
