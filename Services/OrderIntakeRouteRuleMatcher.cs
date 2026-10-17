using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Applies SQL-authoritative route rules after sender/customer matching and parsing.
/// A rule may fill missing route data but never overwrites contradictory parsed data.
/// Ambiguous/tied matches remain planner review items.
///
/// The matcher is deliberately scoped to the regular retailer formats that are stable
/// enough for automatic route assistance: Aldi, Morrisons, Waitrose and Costco.
/// Other inbound orders can still be staged from the parser, but legacy / experimental
/// route rules must not steer those records.
/// </summary>
public static class OrderIntakeRouteRuleMatcher
{
    private static readonly HashSet<string> SupportedRetailerCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALDI",
        "MORRISONS",
        "WAITROSE",
        "COSTCO"
    };

    private static readonly string[] SupportedRetailerNames =
    [
        "ALDI",
        "MORRISONS",
        "MORRISON'S",
        "WAITROSE",
        "WEIGHTROSE",
        "COSTCO"
    ];

    public static async Task<EmailIntakeParseResult> ApplyAsync(TmsDbContext db, EmailIntakeParseResult parsed, CancellationToken ct)
    {
        if (parsed.Orders.Count == 0 || !db.Database.IsRelational()) return parsed;

        var customerCodes = parsed.Orders
            .Select(order => Text(order.Payload, "customerCode"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (customerCodes.Length == 0) return parsed;

        IReadOnlyList<RouteRule> rules;
        try { rules = await LoadRules(db, customerCodes, ct); }
        catch (Exception ex) when (DatabaseObjectUnavailable(ex)) { return parsed; }
        if (rules.Count == 0) return parsed;

        var routed = new List<ParsedEmailOrder>(parsed.Orders.Count);
        foreach (var order in parsed.Orders)
            routed.Add(await ApplyToOrder(db, order, rules, ct));

        return parsed with { Orders = routed };
    }

    private static async Task<ParsedEmailOrder> ApplyToOrder(TmsDbContext db, ParsedEmailOrder order, IReadOnlyList<RouteRule> rules, CancellationToken ct)
    {
        var root = JsonNode.Parse(order.Payload.GetRawText())?.AsObject() ?? new JsonObject();
        var customer = Text(root, "customerCode")?.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(customer)) return order;

        var evidence = Evidence.From(root);
        if (!HasSupportedRetailerEvidence(customer, evidence))
            return order;

        var candidates = rules
            .Where(rule => rule.CustomerCode.Equals(customer, StringComparison.OrdinalIgnoreCase))
            .Where(IsSupportedRetailerRule)
            .Select(rule => Score(rule, evidence))
            .Where(match => match is not null)
            .Select(match => match!)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Rule.Priority)
            .ThenByDescending(match => match.Rule.ConfidenceScore)
            .ToList();
        if (candidates.Count == 0) return order;

        var best = candidates[0];
        var tied = candidates.Skip(1).Any(candidate => candidate.Score == best.Score && candidate.Rule.Priority == best.Rule.Priority);
        var singleRuleForCustomer = rules.Count(rule => rule.CustomerCode.Equals(customer, StringComparison.OrdinalIgnoreCase) && rule.Active && IsSupportedRetailerRule(rule)) == 1;
        var requiresReview = tied || (!singleRuleForCustomer && (best.Score < 70 || best.MatchedDimensions < 2));
        var warnings = order.Warnings.ToList();

        if (!tied)
        {
            await FillMissingSiteValues(db, root, best.Rule, ct);
        }
        if (requiresReview)
        {
            warnings.Add(tied
                ? "More than one supported Aldi/Morrisons/Waitrose/Costco SQL route rule matched with the same score; planner review retained."
                : $"Best supported Aldi/Morrisons/Waitrose/Costco SQL route rule confidence was {best.Score}; missing fields were filled only where blank and planner review was retained.");
        }

        root["orderIntakeRouteRuleId"] = best.Rule.Id.ToString();
        root["orderIntakeRouteConfidenceScore"] = best.Score;
        root["orderIntakeRouteMatchedDimensions"] = best.MatchedDimensions;
        root["orderIntakeRouteRequiresReview"] = requiresReview;
        root["orderIntakeRouteScope"] = "Aldi/Morrisons/Waitrose/Costco only";
        root["orderIntakeRouteExplanation"] = JsonSerializer.SerializeToNode(best.Explanation);
        root["orderIntakeRouteAlternatives"] = JsonSerializer.SerializeToNode(candidates.Take(3).Select(candidate => new
        {
            id = candidate.Rule.Id,
            score = candidate.Score,
            candidate.Rule.CustomerCode,
            candidate.Rule.OriginSiteCode,
            candidate.Rule.OriginSiteName,
            candidate.Rule.RetailerCode,
            candidate.Rule.DestinationSiteCode,
            candidate.Rule.DestinationCode,
            candidate.Rule.DestinationName,
            candidate.Rule.DestinationPostcode
        }).ToList());

        if (requiresReview) root["emailRouteRequiresReview"] = true;
        return order with
        {
            Payload = JsonSerializer.SerializeToElement(root),
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static Match? Score(RouteRule rule, Evidence evidence)
    {
        if (!rule.Active || !IsSupportedRetailerRule(rule)) return null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (rule.EffectiveFrom.HasValue && today < rule.EffectiveFrom.Value) return null;
        if (rule.EffectiveTo.HasValue && today > rule.EffectiveTo.Value) return null;

        var score = 25;
        var matchedDimensions = 0;
        var explanation = new List<string> { $"Customer {rule.CustomerCode} matched." };

        if (!Dimension(rule.OriginSiteCode, evidence.OriginCodes, 25, "origin site code", ref score, ref matchedDimensions, explanation)) return null;
        if (!Dimension(rule.OriginSiteName, evidence.OriginNames, 20, "origin site", ref score, ref matchedDimensions, explanation)) return null;
        if (!Dimension(rule.RetailerCode, evidence.Retailers, 20, "retailer", ref score, ref matchedDimensions, explanation)) return null;
        if (!Dimension(rule.DestinationSiteCode, evidence.DestinationCodes, 30, "destination site code", ref score, ref matchedDimensions, explanation)) return null;
        if (!Dimension(rule.DestinationCode, evidence.DestinationCodes, 35, "destination/depot code", ref score, ref matchedDimensions, explanation)) return null;
        if (!Dimension(rule.DestinationName, evidence.DestinationNames, 20, "destination", ref score, ref matchedDimensions, explanation)) return null;
        if (!Dimension(rule.DestinationPostcode, evidence.Postcodes, 25, "destination postcode", ref score, ref matchedDimensions, explanation)) return null;

        var ruleConfidenceWeight = Math.Clamp(rule.ConfidenceScore, 0, 100) / 10;
        score += ruleConfidenceWeight;
        score += Math.Max(0, 10 - Math.Min(rule.Priority, 10));
        return new Match(rule, Math.Clamp(score, 0, 100), matchedDimensions, explanation);
    }

    private static bool Dimension(string? expected, IReadOnlyCollection<string> actual, int weight, string label, ref int score, ref int matchedDimensions, List<string> explanation)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        if (actual.Count == 0)
        {
            explanation.Add($"Rule expects {label} {expected}, but the email did not provide that dimension.");
            return true;
        }
        if (!actual.Any(value => Equivalent(value, expected))) return false;
        score += weight;
        matchedDimensions++;
        explanation.Add($"{label} matched {expected}.");
        return true;
    }

    private static bool HasSupportedRetailerEvidence(string? customer, Evidence evidence)
    {
        if (IsSupportedValue(customer)) return true;
        if (evidence.Retailers.Any(IsSupportedValue)) return true;
        if (evidence.DestinationCodes.Any(IsSupportedDestinationCode)) return true;
        if (evidence.DestinationNames.Any(IsSupportedValue)) return true;
        return false;
    }

    private static bool IsSupportedRetailerRule(RouteRule rule)
    {
        if (IsSupportedValue(rule.RetailerCode)) return true;
        if (!string.IsNullOrWhiteSpace(rule.DestinationCode) && IsSupportedDestinationCode(rule.DestinationCode)) return true;
        if (!string.IsNullOrWhiteSpace(rule.DestinationSiteCode) && IsSupportedDestinationCode(rule.DestinationSiteCode)) return true;
        if (IsSupportedValue(rule.DestinationName)) return true;
        return false;
    }

    private static bool IsSupportedDestinationCode(string value) =>
        value.StartsWith("ALD", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("MOR", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("WR", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("WAI", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("COS", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("CST", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalised = Normalize(value);
        if (SupportedRetailerCodes.Any(code => normalised.Equals(Normalize(code), StringComparison.Ordinal))) return true;
        return SupportedRetailerNames.Any(name => normalised.Contains(Normalize(name), StringComparison.Ordinal));
    }

    private static async Task FillMissingSiteValues(TmsDbContext db, JsonObject root, RouteRule rule, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Text(root, "collectionSiteCode")) && !string.IsNullOrWhiteSpace(rule.OriginSiteCode))
            root["collectionSiteCode"] = rule.OriginSiteCode;
        if (string.IsNullOrWhiteSpace(Text(root, "collectionSite")) && !string.IsNullOrWhiteSpace(rule.OriginSiteName))
        {
            root["collectionSite"] = rule.OriginSiteName;
            if (string.IsNullOrWhiteSpace(Text(root, "sellerName"))) root["sellerName"] = rule.OriginSiteName;
        }
        if (string.IsNullOrWhiteSpace(Text(root, "deliverySiteCode")) && !string.IsNullOrWhiteSpace(rule.DestinationSiteCode))
            root["deliverySiteCode"] = rule.DestinationSiteCode;
        if (string.IsNullOrWhiteSpace(Text(root, "destinationCode")) && !string.IsNullOrWhiteSpace(rule.DestinationCode))
            root["destinationCode"] = rule.DestinationCode;
        if (string.IsNullOrWhiteSpace(Text(root, "deliverySite")) && string.IsNullOrWhiteSpace(Text(root, "destination")) && string.IsNullOrWhiteSpace(Text(root, "stallNumber")) && !string.IsNullOrWhiteSpace(rule.DestinationName))
        {
            root["deliverySite"] = rule.DestinationName;
            root["destination"] = rule.DestinationName;
            root["stallNumber"] = rule.DestinationName;
        }
        if (string.IsNullOrWhiteSpace(Text(root, "destinationPostcode")) && !string.IsNullOrWhiteSpace(rule.DestinationPostcode))
            root["destinationPostcode"] = rule.DestinationPostcode;
        if (string.IsNullOrWhiteSpace(Text(root, "retailerCode")) && !string.IsNullOrWhiteSpace(rule.RetailerCode))
            root["retailerCode"] = rule.RetailerCode;

        var destinationCode = rule.DestinationSiteCode ?? rule.DestinationCode;
        if (!string.IsNullOrWhiteSpace(destinationCode) && string.IsNullOrWhiteSpace(Text(root, "deliverySiteId")))
        {
            var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(x => x.Active && x.ExternalCode == destinationCode, ct);
            if (site is not null)
            {
                root["deliverySiteId"] = site.Id.ToString();
                root["deliverySiteCode"] = site.ExternalCode;
                if (string.IsNullOrWhiteSpace(Text(root, "deliverySite"))) root["deliverySite"] = site.Name;
                if (string.IsNullOrWhiteSpace(Text(root, "destination"))) root["destination"] = site.Name;
                if (string.IsNullOrWhiteSpace(Text(root, "stallNumber"))) root["stallNumber"] = site.Name;
            }
        }
    }

    private static async Task<IReadOnlyList<RouteRule>> LoadRules(TmsDbContext db, IReadOnlyCollection<string> customerCodes, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            var parameterNames = new List<string>();
            var index = 0;
            foreach (var code in customerCodes)
            {
                var name = $"@c{index++}";
                parameterNames.Add(name);
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = code;
                command.Parameters.Add(parameter);
            }
            command.CommandText = $"SELECT Id, CustomerCode, OriginSiteCode, OriginSiteName, RetailerCode, DestinationSiteCode, DestinationCode, DestinationName, DestinationPostcode, Priority, ConfidenceScore, Active, EffectiveFrom, EffectiveTo FROM dbo.OrderIntakeRouteRules WHERE Active=1 AND CustomerCode IN ({string.Join(',', parameterNames)})";
            var result = new List<RouteRule>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var rule = new RouteRule(
                    reader.GetGuid(0), reader.GetString(1), NullString(reader, 2), NullString(reader, 3), NullString(reader, 4),
                    NullString(reader, 5), NullString(reader, 6), NullString(reader, 7), NullString(reader, 8), reader.GetInt32(9),
                    reader.GetInt32(10), reader.GetBoolean(11), NullDate(reader, 12), NullDate(reader, 13));
                if (IsSupportedRetailerRule(rule))
                    result.Add(rule);
            }
            return result;
        }
        finally { if (openedHere) await connection.CloseAsync(); }
    }

    private static string? NullString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateOnly? NullDate(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateOnly.FromDateTime(reader.GetDateTime(ordinal));

    private static bool Equivalent(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        return a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? Text(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
            value = payload.EnumerateObject().FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;
    }

    private static string? Text(JsonObject payload, string name)
    {
        var value = payload.FirstOrDefault(property => property.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        return value is JsonValue json && json.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    private static bool DatabaseObjectUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RouteRule(Guid Id, string CustomerCode, string? OriginSiteCode, string? OriginSiteName, string? RetailerCode, string? DestinationSiteCode, string? DestinationCode, string? DestinationName, string? DestinationPostcode, int Priority, int ConfidenceScore, bool Active, DateOnly? EffectiveFrom, DateOnly? EffectiveTo);
    private sealed record Match(RouteRule Rule, int Score, int MatchedDimensions, IReadOnlyList<string> Explanation);

    private sealed record Evidence(IReadOnlyCollection<string> OriginCodes, IReadOnlyCollection<string> OriginNames, IReadOnlyCollection<string> Retailers, IReadOnlyCollection<string> DestinationCodes, IReadOnlyCollection<string> DestinationNames, IReadOnlyCollection<string> Postcodes)
    {
        public static Evidence From(JsonObject root)
        {
            var destinationCodes = Values(root, "deliverySiteCode", "destinationCode", "depotCode", "depotId").ToList();
            var retailers = Values(root, "retailerCode", "retailer", "marketName").ToList();
            foreach (var code in destinationCodes)
            {
                if (code.StartsWith("ALD", StringComparison.OrdinalIgnoreCase)) retailers.Add("ALDI");
                if (code.StartsWith("MOR", StringComparison.OrdinalIgnoreCase)) retailers.Add("MORRISONS");
                if (code.StartsWith("WR", StringComparison.OrdinalIgnoreCase) || code.StartsWith("WAI", StringComparison.OrdinalIgnoreCase)) retailers.Add("WAITROSE");
                if (code.StartsWith("COS", StringComparison.OrdinalIgnoreCase) || code.StartsWith("CST", StringComparison.OrdinalIgnoreCase)) retailers.Add("COSTCO");
            }
            return new Evidence(
                Values(root, "collectionSiteCode", "originSiteCode"),
                Values(root, "collectionSite", "originSite", "sellerName"),
                retailers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                destinationCodes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Values(root, "deliverySite", "destination", "depotName", "stallNumber"),
                Values(root, "destinationPostcode", "deliveryPostcode", "postcode"));
        }

        private static IReadOnlyCollection<string> Values(JsonObject root, params string[] names) => names
            .Select(name => Text(root, name))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
