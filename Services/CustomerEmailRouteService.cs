using System.Net.Mail;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Applies planner-approved sender mappings from SQL master data. Sender/customer
/// mappings may fill missing collection/delivery defaults, but they must never overwrite
/// a collection or destination that was explicitly parsed from the email.
/// </summary>
public static class CustomerEmailRouteService
{
    private static readonly ConcurrentDictionary<string, CachedRouteCandidates> RouteCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RouteCacheLifetime = TimeSpan.FromMinutes(1);

    /// <summary>True only for an unambiguous, planner-approved sender/customer mapping.</summary>
    public static async Task<bool> HasApprovedRouteAsync(TmsDbContext db, MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        var match = await FindRouteAsync(db, request, ct);
        return match is { RequiresReview: false, Conflicting: false };
    }

    public static void InvalidateCache() => RouteCache.Clear();

    public static async Task<EmailIntakeParseResult> ApplyAsync(
        TmsDbContext db,
        EmailIntakeParseResult parsed,
        MailboxEmailIntakeRequest request,
        CancellationToken ct)
    {
        if (parsed.Orders.Count == 0)
            return parsed;

        var sender = NormalizeEmail(request.SenderAddress);
        if (sender is null)
            return await OrderIntakeRouteRuleMatcher.ApplyAsync(db, parsed, ct);

        var route = await FindRouteAsync(db, request, ct);
        if (route is null)
            return await OrderIntakeRouteRuleMatcher.ApplyAsync(db, parsed, ct);

        if (route.Conflicting)
        {
            var conflicted = parsed with
            {
                Orders = parsed.Orders.Select(order =>
                {
                    var root = JsonNode.Parse(order.Payload.GetRawText())?.AsObject() ?? new JsonObject();
                    root["plannerReady"] = false;
                    root["emailRouteRequiresReview"] = true;
                    return order with { Payload = JsonSerializer.SerializeToElement(root) };
                }).ToList(),
                Warnings = parsed.Warnings.Append(
                    $"Sender {sender} has conflicting CRM routes/customer mappings in SQL. Planner review is required before this sender can be automated.")
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
            return await OrderIntakeRouteRuleMatcher.ApplyAsync(db, conflicted, ct);
        }

        Site? collectionSite = null;
        Site? deliverySite = null;
        if (!string.IsNullOrWhiteSpace(route.DefaultSiteCode))
        {
            collectionSite = await db.Sites.AsNoTracking().FirstOrDefaultAsync(
                item => item.Active && item.ExternalCode == route.DefaultSiteCode, ct);
        }
        if (!string.IsNullOrWhiteSpace(route.DefaultDeliverySiteCode))
        {
            deliverySite = await db.Sites.AsNoTracking().FirstOrDefaultAsync(
                item => item.Active && item.ExternalCode == route.DefaultDeliverySiteCode, ct);
        }

        var routed = new List<ParsedEmailOrder>(parsed.Orders.Count);
        var globalWarnings = parsed.Warnings.ToList();
        foreach (var order in parsed.Orders)
        {
            var root = JsonNode.Parse(order.Payload.GetRawText())?.AsObject() ?? new JsonObject();
            var warnings = order.Warnings.ToList();
            var conflict = ApplyCustomerMapping(root, route.Route, collectionSite, deliverySite, sender, warnings);
            if (route.RequiresReview || conflict)
                root["plannerReady"] = false;
            routed.Add(order with
            {
                Payload = JsonSerializer.SerializeToElement(root),
                Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            });
        }

        if (route.RequiresReview)
            globalWarnings.Add($"SQL sender/customer mapping for {sender} is marked Requires Review.");

        var senderMapped = parsed with
        {
            Orders = routed,
            Warnings = globalWarnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        return await OrderIntakeRouteRuleMatcher.ApplyAsync(db, senderMapped, ct);
    }

    private static async Task<RouteMatch?> FindRouteAsync(TmsDbContext db, MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        var sender = NormalizeEmail(request.SenderAddress);
        if (sender is null) return null;
        var domain = sender[(sender.IndexOf('@') + 1)..];
        var key = $"{sender}|{request.Subject?.Trim()}";
        if (!RouteCache.TryGetValue(key, out var cached) || cached.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            try
            {
                var routes = await db.CustomerEmailRoutes.AsNoTracking()
                    .Where(route => route.Active && (route.SenderEmail == sender || route.SenderDomain == domain))
                    .ToListAsync(ct);
                cached = new CachedRouteCandidates(routes, DateTimeOffset.UtcNow.Add(RouteCacheLifetime));
                RouteCache[key] = cached;
            }
            catch (Exception ex) when (DatabaseObjectUnavailable(ex)) { return null; }
        }

        var subject = request.Subject ?? string.Empty;
        var matches = cached.Routes.Select(route => new { Route = route, Score = Score(route, sender, domain, subject) })
            .Where(match => match.Score >= 0).OrderByDescending(match => match.Score).ThenBy(match => match.Route.Id).ToList();
        if (matches.Count == 0) return null;
        var bestScore = matches[0].Score;
        var best = matches.Where(match => match.Score == bestScore).Select(match => match.Route).ToList();
        var customers = best.Select(route => route.CustomerCode.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return customers.Count == 1 ? new RouteMatch(best[0], false) : new RouteMatch(best[0], true);
    }

    /// <summary>
    /// Learns customer identity only from an order a planner has approved. Route/origin/
    /// destination learning belongs in OrderIntakeRouteRules and must not be inferred from
    /// a sender address alone.
    /// </summary>
    public static async Task LearnFromApprovedOrderAsync(
        TmsDbContext db,
        JsonElement payload,
        string customerCode,
        CancellationToken ct)
    {
        var sender = NormalizeEmail(Text(payload, "sourceSender") ?? Text(payload, "senderAddress"));
        if (sender is null || sender.EndsWith("@lyonshaulage.com", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var existing = await db.CustomerEmailRoutes
                .Where(route => route.Active && route.SenderEmail == sender && route.SubjectContains == null)
                .OrderBy(route => route.Id)
                .FirstOrDefaultAsync(ct);
            var normalCustomer = customerCode.Trim().ToUpperInvariant();
            if (existing is null)
            {
                existing = new CustomerEmailRoute
                {
                    CustomerCode = normalCustomer,
                    SenderEmail = sender,
                    SenderDomain = sender[(sender.IndexOf('@') + 1)..],
                    ParserType = Clip(Text(payload, "parserTemplate") ?? Text(payload, "mappingTemplate"), 120),
                    DefaultSiteCode = null,
                    DefaultDeliverySiteCode = null,
                    MarketKey = null,
                    RequiresReview = false,
                    Active = true
                };
                db.CustomerEmailRoutes.Add(existing);
            }
            else if (!string.Equals(existing.CustomerCode, normalCustomer, StringComparison.OrdinalIgnoreCase))
            {
                existing.RequiresReview = true;
            }

            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "CustomerEmailRoute",
                EntityId = existing.Id,
                Action = existing.RequiresReview ? "EmailCustomerMappingConflict" : "EmailCustomerMappingLearnedFromApprovedOrder",
                ChangedBy = "Order approval",
                ChangesJson = JsonSerializer.Serialize(new
                {
                    senderEmail = sender,
                    customerCode = normalCustomer,
                    existing.RequiresReview
                })
            });
            InvalidateCache();
        }
        catch (Exception ex) when (DatabaseObjectUnavailable(ex))
        {
            // Approval remains available if optional mapping infrastructure is unavailable.
        }
    }

    private static bool ApplyCustomerMapping(JsonObject root, CustomerEmailRoute route, Site? collectionSite, Site? deliverySite, string sender, List<string> warnings)
    {
        var conflict = false;
        var currentCustomer = Text(root, "customerCode");
        if (IsUnmapped(currentCustomer))
            root["customerCode"] = route.CustomerCode.Trim().ToUpperInvariant();
        else if (!string.Equals(currentCustomer, route.CustomerCode, StringComparison.OrdinalIgnoreCase))
        {
            conflict = true;
            warnings.Add($"Parsed customer {currentCustomer} conflicts with SQL sender mapping {route.CustomerCode}; planner review retained.");
        }

        if (collectionSite is not null)
        {
            var currentCode = Text(root, "collectionSiteCode");
            var currentName = Text(root, "collectionSite") ?? Text(root, "sellerName");
            if (string.IsNullOrWhiteSpace(currentCode) && string.IsNullOrWhiteSpace(currentName))
            {
                root["collectionSiteCode"] = collectionSite.ExternalCode;
                root["collectionSiteId"] = collectionSite.Id.ToString();
                root["collectionSite"] = collectionSite.Name;
                root["sellerName"] = collectionSite.Name;
            }
            else if (!string.IsNullOrWhiteSpace(currentCode)
                && !string.Equals(currentCode, collectionSite.ExternalCode, StringComparison.OrdinalIgnoreCase))
            {
                conflict = true;
                warnings.Add($"Parsed collection site {currentCode} conflicts with SQL sender mapping {collectionSite.ExternalCode}; planner review retained.");
            }
        }
        if (deliverySite is not null)
        {
            var currentCode = Text(root, "deliverySiteCode");
            var currentName = Text(root, "deliverySite") ?? Text(root, "destination") ?? Text(root, "stallNumber");
            if (string.IsNullOrWhiteSpace(currentCode) && string.IsNullOrWhiteSpace(currentName))
            {
                root["deliverySiteCode"] = deliverySite.ExternalCode;
                root["deliverySiteId"] = deliverySite.Id.ToString();
                root["deliverySite"] = deliverySite.Name;
                root["destination"] = deliverySite.Name;
                root["stallNumber"] = deliverySite.Name;
            }
            else if (!string.IsNullOrWhiteSpace(currentCode) && !string.Equals(currentCode, deliverySite.ExternalCode, StringComparison.OrdinalIgnoreCase))
            {
                conflict = true;
                warnings.Add($"Parsed delivery site {currentCode} conflicts with SQL sender mapping {deliverySite.ExternalCode}; planner review retained.");
            }
        }

        var hasRouteDefaults = collectionSite is not null || deliverySite is not null;
        root["emailRouteMatched"] = true;
        root["emailRouteId"] = route.Id.ToString();
        root["emailRouteSender"] = sender;
        root["emailRouteCustomerCode"] = route.CustomerCode;
        root["emailRouteDefaultSiteCode"] = route.DefaultSiteCode;
        root["emailRouteDefaultDeliverySiteCode"] = route.DefaultDeliverySiteCode;
        root["emailRouteRequiresReview"] = route.RequiresReview || conflict;
        root["emailRouteIdentityOnly"] = !hasRouteDefaults;
        return conflict;
    }

    private static int Score(CustomerEmailRoute route, string sender, string domain, string subject)
    {
        var exact = NormalizeEmail(route.SenderEmail);
        var routeDomain = NormalizeDomain(route.SenderDomain);
        var addressMatches = exact is not null && string.Equals(exact, sender, StringComparison.OrdinalIgnoreCase);
        var domainMatches = routeDomain is not null &&
            (string.Equals(domain, routeDomain, StringComparison.OrdinalIgnoreCase)
             || domain.EndsWith($".{routeDomain}", StringComparison.OrdinalIgnoreCase));
        if (!addressMatches && !domainMatches) return -1;
        if (!string.IsNullOrWhiteSpace(route.SubjectContains)
            && !subject.Contains(route.SubjectContains.Trim(), StringComparison.OrdinalIgnoreCase)) return -1;
        return (addressMatches ? 10000 : 1000) + (route.SubjectContains?.Trim().Length ?? 0);
    }

    internal static string? NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var address = new MailAddress(value.Trim()).Address.Trim().ToLowerInvariant();
            return address.Contains('@') ? address : null;
        }
        catch (FormatException) { return null; }
    }

    private static string? NormalizeDomain(string? value)
    {
        var result = value?.Trim().TrimStart('@').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static bool IsUnmapped(string? value) => string.IsNullOrWhiteSpace(value)
        || value.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
        || value.Equals("UNMAPPED", StringComparison.OrdinalIgnoreCase)
        || value.Equals("GENERAL", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
        {
            value = payload.EnumerateObject()
                .FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
        }
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;
    }

    private static string? Text(JsonObject payload, string name)
    {
        var value = payload.FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    private static bool DatabaseObjectUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Length <= length ? value : value[..length];

    private sealed record CachedRouteCandidates(IReadOnlyList<CustomerEmailRoute> Routes, DateTimeOffset ExpiresAtUtc);
    private sealed record RouteMatch(CustomerEmailRoute Route, bool Conflicting)
    {
        public bool RequiresReview => Route.RequiresReview;
        public string? SubjectContains => Route.SubjectContains;
        public string CustomerCode => Route.CustomerCode;
        public string? DefaultSiteCode => Route.DefaultSiteCode;
        public string? DefaultDeliverySiteCode => Route.DefaultDeliverySiteCode;
        public Guid Id => Route.Id;
    }
}
