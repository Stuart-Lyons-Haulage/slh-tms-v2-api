using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Fixed-format bookings (the same customer sending the same spreadsheet or email layout
/// every day) do not need a planner to look at every row. Each parsed order is compared
/// with that format's recent history; anything unusual (a new depot, a pallet count well
/// outside the normal range, a new collection site) is written onto the card as a warning.
///
/// A format can then be set per format to:
///   Off   - always reviewed by a planner;
///   Trial - still reviewed, but each card says whether it would have auto-approved, so
///           the planners can check the rule against what they actually do;
///   On    - clean orders (no warnings, nothing unusual) are approved straight into
///           planning, with the decision recorded in the audit trail.
/// </summary>
public static class IntakeAutoApproval
{
    public const string ModeOff = "Off";
    public const string ModeTrial = "Trial";
    public const string ModeOn = "On";
    public const string SettingsKey = "setting:intake-auto-approval";
    public const string Actor = "Auto-approval (trusted format)";
    private const string AnomalyPrefix = "Unusual: ";
    private const int HistoryDays = 56;
    private const int MinimumHistory = 5;
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, FormatHistory History)> HistoryCache = new(StringComparer.Ordinal);

    /// <summary>Which recurring format an order came from.</summary>
    public static string? FormatKey(JsonElement payload, string? sourceKey = null)
    {
        var profile = Text(payload, "intakeProfile") ?? Text(payload, "intakeParser");
        if (!string.IsNullOrWhiteSpace(profile)) return profile.Trim();
        // The generic "best guess" body reader is never a fixed format.
        if (string.Equals(sourceKey, "body-1", StringComparison.OrdinalIgnoreCase)) return null;
        var sender = Text(payload, "sourceSender");
        var domain = sender?.Contains('@') == true ? sender[(sender.IndexOf('@') + 1)..].Trim().ToLowerInvariant() : null;
        var customer = Text(payload, "customerCode");
        return string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(customer) ? null : $"{domain}|{customer.ToUpperInvariant()}";
    }

    public static async Task<Dictionary<string, string>> LoadModesAsync(TmsDbContext db, CancellationToken ct)
    {
        var json = await db.StagedImports.AsNoTracking()
            .Where(x => x.IdempotencyKey == SettingsKey)
            .Select(x => x.PayloadJson)
            .FirstOrDefaultAsync(ct);
        var modes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return modes;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("modes", out var values) && values.ValueKind == JsonValueKind.Object)
                foreach (var property in values.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String)
                        modes[property.Name] = NormaliseMode(property.Value.GetString());
        }
        catch (JsonException) { }
        return modes;
    }

    public static async Task SaveModeAsync(TmsDbContext db, string formatKey, string mode, string? user, CancellationToken ct)
    {
        var modes = await LoadModesAsync(db, ct);
        modes[formatKey] = NormaliseMode(mode);
        var payload = JsonSerializer.Serialize(new { modes, updatedAtUtc = DateTimeOffset.UtcNow, updatedBy = user });
        var row = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == SettingsKey, ct);
        if (row is null)
        {
            row = new StagedImport { EntityType = "setting", IdempotencyKey = SettingsKey, PayloadJson = payload, Status = StagingStatus.Promoted, Source = "Order Control" };
            db.StagedImports.Add(row);
        }
        else
        {
            row.PayloadJson = payload;
        }
        row.ReviewedAtUtc = DateTimeOffset.UtcNow;
        row.ReviewedBy = user;
        db.StagedImportEvents.Add(StagingAudit.Create(row, "AutoApprovalModeChanged", row.Status, $"{formatKey} set to {NormaliseMode(mode)}", user));
        await db.SaveChangesAsync(ct);
    }

    public static string NormaliseMode(string? mode) =>
        string.Equals(mode, ModeOn, StringComparison.OrdinalIgnoreCase) ? ModeOn
        : string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase) ? ModeOff
        : ModeTrial;

    /// <summary>Default for a format nobody has set yet: fixed formats start in Trial.</summary>
    public static string ModeFor(IReadOnlyDictionary<string, string> modes, string? formatKey) =>
        formatKey is null ? ModeOff : modes.TryGetValue(formatKey, out var mode) ? mode : ModeTrial;

    /// <summary>Compare each order with its format's history, record anything unusual on
    /// the card, and stamp whether it is clean enough to approve itself.</summary>
    public static async Task<EmailIntakeParseResult> AnnotateAsync(TmsDbContext db, EmailIntakeParseResult parsed, CancellationToken ct)
    {
        if (parsed.Orders.Count == 0) return parsed;
        var modes = await LoadModesAsync(db, ct);
        var orders = new List<ParsedEmailOrder>(parsed.Orders.Count);
        foreach (var order in parsed.Orders)
        {
            var key = FormatKey(order.Payload, order.SourceKey);
            var history = key is null ? null : await HistoryAsync(db, key, ct);
            orders.Add(Annotate(order, key, history, ModeFor(modes, key)));
        }
        return parsed with { Orders = orders };
    }

    internal static ParsedEmailOrder Annotate(ParsedEmailOrder order, string? key, FormatHistory? history, string mode)
    {
        var root = JsonNode.Parse(order.Payload.GetRawText())?.AsObject();
        if (root is null) return order;
        List<string> anomalies = key is null || history is null ? [] : Anomalies(order.Payload, history);
        var warnings = root["intakeWarnings"] as JsonArray ?? [];
        foreach (var anomaly in anomalies) warnings.Add($"{AnomalyPrefix}{anomaly}");
        root["intakeWarnings"] = warnings;
        if (anomalies.Count > 0) root["anomalies"] = new JsonArray(anomalies.Select(item => (JsonNode?)item).ToArray());

        var blockers = new List<string>();
        if (key is null) blockers.Add("Not a fixed booking format");
        else if (history is null || history.Orders < MinimumHistory) blockers.Add("Not enough approved history for this format yet");
        if (warnings.Count > 0) blockers.Add("Has warnings or unusual lines");
        if (Int(order.Payload, "pallets") is not > 0) blockers.Add("No quantity");
        if (Text(order.Payload, "collectionDate") is null) blockers.Add("No collection date");
        if (string.IsNullOrWhiteSpace(Text(order.Payload, "sellerName") ?? Text(order.Payload, "collectionSite"))) blockers.Add("No collection site");
        if (string.IsNullOrWhiteSpace(Text(order.Payload, "stallNumber") ?? Text(order.Payload, "destination"))) blockers.Add("No delivery site");
        if (Bool(order.Payload, "plannerReady") == false) blockers.Add("Not planner-ready");

        root["autoApproval"] = new JsonObject
        {
            ["formatKey"] = key,
            ["mode"] = mode,
            ["eligible"] = blockers.Count == 0,
            ["blockers"] = new JsonArray(blockers.Select(item => (JsonNode?)item).ToArray()),
            ["historyOrders"] = history?.Orders ?? 0
        };
        if (anomalies.Count > 0 && Text(order.Payload, "intakeConfidence") == "High") root["intakeConfidence"] = "Medium";
        var updatedWarnings = order.Warnings.Concat(anomalies.Select(item => $"{AnomalyPrefix}{item}")).ToList();
        return order with { Payload = JsonSerializer.SerializeToElement(root), Warnings = updatedWarnings };
    }

    internal static List<string> Anomalies(JsonElement payload, FormatHistory history)
    {
        var result = new List<string>();
        if (history.Orders < MinimumHistory) return result;
        var pallets = Int(payload, "pallets");
        var displayDestination = Text(payload, "stallNumber") ?? Text(payload, "destination");
        var destinationIdentities = DestinationIdentities(payload).ToList();
        var collectionIdentities = CollectionIdentities(payload).ToList();
        var canonical = destinationIdentities
            .Select(identity => history.DestinationAliases.TryGetValue(identity, out var known) ? known : history.Destinations.ContainsKey(identity) ? identity : null)
            .FirstOrDefault(value => value is not null);

        if (destinationIdentities.Count > 0 && canonical is null)
            result.Add($"new delivery point {displayDestination} for this booking");
        if (collectionIdentities.Count > 0 && history.Collections.Count > 0 && !collectionIdentities.Any(history.Collections.Contains))
            result.Add($"new collection site {Text(payload, "sellerName") ?? Text(payload, "collectionSite")}");
        if (pallets is > 0 && canonical is not null &&
            history.Destinations.TryGetValue(canonical, out var counts) && counts.Count >= 3)
        {
            var min = counts.Min();
            var max = counts.Max();
            if (pallets > Math.Max(max * 1.25, max + 2) || pallets < Math.Min(min * 0.75, min - 2))
                result.Add($"{displayDestination} {pallets} {UnitWord(payload)}, usually {(min == max ? min.ToString(CultureInfo.InvariantCulture) : $"{min}–{max}")}");
        }
        return result;
    }

    /// <summary>Approve the clean orders of formats switched On. Each is a normal approval
    /// through the staging service, recorded against the auto-approval actor.</summary>
    public static async Task<int> PromoteAsync(TmsDbContext db, StagingService staging, IEnumerable<StagedImport> created, ILogger logger, CancellationToken ct)
    {
        var approved = 0;
        var actor = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Actor)], "auto-approval"));
        foreach (var item in created.ToList())
        {
            try
            {
                using var document = JsonDocument.Parse(item.PayloadJson);
                if (!document.RootElement.TryGetProperty("autoApproval", out var decision) ||
                    Text(decision, "mode") != ModeOn || Bool(decision, "eligible") != true)
                    continue;
                var promoted = await staging.ReviewAndPromote(item.Id, true, "Auto-approved: fixed booking format, nothing unusual against recent history.", actor, ct);
                if (promoted.Status == StagingStatus.Promoted) approved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Anything that cannot approve itself simply stays in review.
                logger.LogWarning(ex, "Auto-approval left staged order {StagedImportId} for planner review.", item.Id);
                db.ChangeTracker.Clear();
            }
        }
        return approved;
    }

    internal sealed class FormatHistory
    {
        public int Orders { get; set; }
        public Dictionary<string, List<int>> Destinations { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Collections { get; } = new(StringComparer.Ordinal);
        // Every name or master-site id a destination has been seen under, mapped to the
        // destination's canonical key, so a raw name and its master name match.
        public Dictionary<string, string> DestinationAliases { get; } = new(StringComparer.Ordinal);
    }

    // A site is identified by its master-data id when matched, otherwise by its names.
    private static IEnumerable<string> DestinationIdentities(JsonElement payload) =>
        new[] { Text(payload, "deliverySiteId"), Text(payload, "deliverySiteCode"), Text(payload, "stallNumber"), Text(payload, "deliverySite"), Text(payload, "destination") }
            .Select(Clean).Where(value => value.Length > 0).Distinct();

    private static IEnumerable<string> CollectionIdentities(JsonElement payload) =>
        new[] { Text(payload, "collectionSiteId"), Text(payload, "collectionSiteCode"), Text(payload, "sellerName"), Text(payload, "collectionSite") }
            .Select(Clean).Where(value => value.Length > 0).Distinct();

    private static async Task<FormatHistory> HistoryAsync(TmsDbContext db, string key, CancellationToken ct)
    {
        if (HistoryCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(10))
            return cached.History;

        var since = DateTimeOffset.UtcNow.AddDays(-HistoryDays);
        // The format key is part of every payload (profile/parser name or sender), which
        // narrows the scan before the payloads are read.
        var needle = key.Contains('|') ? key[..key.IndexOf('|')] : key;
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == "order" && x.ReceivedAtUtc >= since &&
                        (x.Status == StagingStatus.Promoted || x.Status == StagingStatus.Approved) &&
                        x.PayloadJson.Contains(needle))
            .OrderByDescending(x => x.ReceivedAtUtc)
            .Take(3000)
            .Select(x => x.PayloadJson)
            .ToListAsync(ct);

        var history = new FormatHistory();
        foreach (var json in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var payload = document.RootElement;
                if (!string.Equals(FormatKey(payload), key, StringComparison.OrdinalIgnoreCase)) continue;
                history.Orders++;
                foreach (var identity in CollectionIdentities(payload)) history.Collections.Add(identity);
                var identities = DestinationIdentities(payload).ToList();
                if (identities.Count == 0) continue;
                var canonical = identities.Select(identity => history.DestinationAliases.TryGetValue(identity, out var known) ? known : null).FirstOrDefault(value => value is not null) ?? identities[0];
                foreach (var identity in identities) history.DestinationAliases[identity] = canonical;
                if (!history.Destinations.TryGetValue(canonical, out var counts)) history.Destinations[canonical] = counts = [];
                if (Int(payload, "pallets") is int pallets && pallets > 0) counts.Add(pallets);
            }
            catch (JsonException) { }
        }
        HistoryCache[key] = (DateTimeOffset.UtcNow, history);
        return history;
    }

    internal static void ClearCache() => HistoryCache.Clear();

    private static string UnitWord(JsonElement payload)
    {
        var unit = (Text(payload, "palletType") ?? string.Empty).ToLowerInvariant();
        return unit.Contains("trolley") ? "trolleys" : unit.Contains("tray") ? "trays" : unit.Contains("crate") ? "crates" : "pallets";
    }

    private static string Clean(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? Text(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            }
            : null;

    private static int? Int(JsonElement payload, string name) =>
        int.TryParse(Text(payload, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool? Bool(JsonElement payload, string name) =>
        bool.TryParse(Text(payload, name), out var value) ? value : null;
}
