using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class SitePlanningProfileStore
{
    private const string ProfileType = "siteplanningprofile";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static async Task SyncOrderProfilesAsync(TmsDbContext db, DateOnly date, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        if (sites.Count == 0) return;
        var profiles = await ReadProfilesAsync(db, sites, ct);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => (x.EntityType == "order" || x.EntityType == "register:order") && x.Status != StagingStatus.Rejected)
            .OrderByDescending(x => x.ReceivedAtUtc).Take(8000).ToListAsync(ct);

        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                if (DateOnly.TryParse(Text(root, "collectionDate"), out var collectionDate) && collectionDate != date) continue;
                var temperature = ParseTemperature(Text(root, "temperature", "temperatureC", "temp", "temperatureRequirement") ?? Tagged(Text(root, "driverInstructions", "notes"), "Temperature"));
                if (temperature is null) continue;
                var collection = Text(root, "collectionLocation", "collectionSite", "collection", "sellerName", "pickupLocation", "pickupSite");
                var site = MatchSite(sites, collection);
                if (site is null) continue;
                profiles.TryGetValue(site.Id, out var existing);
                if (existing?.DefaultTemperatureC is not null) continue;
                var profile = new SitePlanningProfile(site.Id, site.ExternalCode, site.Name, temperature, existing?.Region ?? InferRegion(site.CollectionAddress ?? site.Name), site.CollectionAddress);
                await SaveProfileAsync(db, profile, "Auto-learned from live order temperature", ct);
                profiles[site.Id] = profile;
            }
            catch (JsonException) { }
        }
    }

    public static async Task<TemperatureSyncResult> ApplyDailyRunTemperaturesAsync(TmsDbContext db, DateOnly date, CancellationToken ct)
    {
        var conflicts = new List<TemperatureConflict>();
        var updatedLoads = 0;
        var updatedOrders = 0;
        List<Load> loads;
        try
        {
            loads = await db.Loads.Include(x => x.Stops).Where(x => x.PlanningDate == date && x.Status != LoadStatus.Cancelled).ToListAsync(ct);
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            return new TemperatureSyncResult(0, 0, conflicts);
        }

        var orderIds = loads.SelectMany(x => x.Stops).Where(x => x.OrderId is not null).Select(x => x.OrderId!.Value).Distinct().ToList();
        var orders = orderIds.Count == 0 ? [] : await db.TransportOrders.Where(x => orderIds.Contains(x.Id) && x.Status != OrderStatus.Cancelled).ToListAsync(ct);
        var orderById = orders.ToDictionary(x => x.Id);

        foreach (var load in loads)
        {
            var loadOrders = load.Stops.Where(x => x.OrderId is not null && orderById.ContainsKey(x.OrderId.Value)).Select(x => orderById[x.OrderId!.Value]).DistinctBy(x => x.Id).ToList();
            if (loadOrders.Count == 0) continue;
            var context = await ResolveRunTemperaturesAsync(db, loadOrders, ct);
            if (context.HasConflict)
            {
                load.TemperatureC = null;
                conflicts.Add(new TemperatureConflict(load.Id, load.Reference, context.DistinctTemperatures.Select(FormatTemperature).ToList()));
                foreach (var order in loadOrders)
                {
                    if (!context.OrderTemperatures.TryGetValue(order.Id, out var temperature) || temperature is null) continue;
                    var next = WithTemperatureInstruction(order.DriverInstructions, temperature.Value, false);
                    if (next == order.DriverInstructions) continue;
                    order.DriverInstructions = next;
                    updatedOrders++;
                }
                continue;
            }

            if (context.LoadTemperatureC is null) continue;
            var loadTemperature = context.LoadTemperatureC.Value;
            if (load.TemperatureC != loadTemperature)
            {
                load.TemperatureC = loadTemperature;
                updatedLoads++;
            }
            foreach (var order in loadOrders)
            {
                var next = WithTemperatureInstruction(order.DriverInstructions, loadTemperature, true);
                if (next == order.DriverInstructions) continue;
                order.DriverInstructions = next;
                updatedOrders++;
            }
        }

        if (updatedLoads > 0 || updatedOrders > 0 || conflicts.Count > 0) await db.SaveChangesAsync(ct);
        return new TemperatureSyncResult(updatedLoads, updatedOrders, conflicts);
    }

    public static async Task<RunTemperatureContext> ResolveRunTemperaturesAsync(TmsDbContext db, IEnumerable<TransportOrder> orders, CancellationToken ct)
    {
        var orderList = orders.ToList();
        var references = orderList.Select(x => Normalise(x.Reference)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staged = new Dictionary<string, (decimal? Temperature, string? Collection)>(StringComparer.OrdinalIgnoreCase);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => (x.EntityType == "order" || x.EntityType == "register:order") && x.Status != StagingStatus.Rejected)
            .OrderByDescending(x => x.ReceivedAtUtc).Take(10000).ToListAsync(ct);
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                var reference = Text(root, "poNumber", "reference", "orderReference", "orderRef");
                if (string.IsNullOrWhiteSpace(reference)) continue;
                var key = Normalise(reference);
                if (!references.Contains(key) || staged.ContainsKey(key)) continue;
                staged[key] = (
                    ParseTemperature(Text(root, "temperature", "temperatureC", "temp", "temperatureRequirement") ?? Tagged(Text(root, "driverInstructions", "notes"), "Temperature")),
                    Text(root, "collectionLocation", "collectionSite", "collection", "sellerName", "pickupLocation", "pickupSite"));
            }
            catch (JsonException) { }
        }

        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var profiles = await ReadProfilesAsync(db, sites, ct);
        var result = new Dictionary<Guid, decimal?>();
        foreach (var order in orderList)
        {
            var explicitTemperature = ParseTemperature(Tagged(order.DriverInstructions, "Temperature") ?? Tagged(order.DriverInstructions, "Load temperature") ?? Tagged(order.DriverInstructions, "Order temperature"));
            staged.TryGetValue(Normalise(order.Reference), out var detail);
            var temperature = explicitTemperature ?? detail.Temperature;
            if (temperature is null)
            {
                var site = MatchSite(sites, detail.Collection ?? order.SellerName);
                if (site is not null && profiles.TryGetValue(site.Id, out var profile)) temperature = profile.DefaultTemperatureC;
            }
            result[order.Id] = temperature;
        }
        var distinct = result.Values.Where(x => x is not null).Select(x => x!.Value).Distinct().OrderBy(x => x).ToList();
        return new RunTemperatureContext(result, distinct.Count == 1 ? distinct[0] : null, distinct.Count > 1, distinct);
    }

    public static async Task<Dictionary<string, string>> ResolveRegionsAsync(TmsDbContext db, IEnumerable<string> destinations, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var profiles = await ReadProfilesAsync(db, sites, ct);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in destinations.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var site = MatchSite(sites, destination);
            var region = site is not null && profiles.TryGetValue(site.Id, out var profile) && !string.IsNullOrWhiteSpace(profile.Region)
                ? profile.Region
                : InferRegion(site?.CollectionAddress ?? destination);
            result[destination] = region;
        }
        return result;
    }

    public static string FormatTemperature(decimal value) => $"{(value > 0 ? "+" : string.Empty)}{value:0.#}°C";

    private static string WithTemperatureInstruction(string? existing, decimal temperature, bool singleLoadTemperature)
    {
        var kept = (existing ?? string.Empty).Split(new[] { '·', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !x.StartsWith("Load temperature:", StringComparison.OrdinalIgnoreCase)
                && !x.StartsWith("Order temperature:", StringComparison.OrdinalIgnoreCase)
                && !x.StartsWith("Set trailer to ", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var formatted = FormatTemperature(temperature);
        kept.Insert(0, singleLoadTemperature ? $"Load temperature: {formatted}" : $"Order temperature: {formatted}");
        if (singleLoadTemperature) kept.Insert(1, $"Set trailer to {formatted} before collection");
        return string.Join(" · ", kept);
    }

    private static async Task<Dictionary<Guid, SitePlanningProfile>> ReadProfilesAsync(TmsDbContext db, IReadOnlyCollection<Site> sites, CancellationToken ct)
    {
        var result = new Dictionary<Guid, SitePlanningProfile>();
        var rows = await db.StagedImports.AsNoTracking().Where(x => x.EntityType == ProfileType && x.Status == StagingStatus.Promoted)
            .OrderByDescending(x => x.ReviewedAtUtc ?? x.ReceivedAtUtc).Take(5000).ToListAsync(ct);
        foreach (var row in rows)
        {
            try
            {
                var profile = JsonSerializer.Deserialize<SitePlanningProfile>(row.PayloadJson, JsonOptions);
                if (profile is null || result.ContainsKey(profile.SiteId)) continue;
                result[profile.SiteId] = profile;
            }
            catch (JsonException) { }
        }
        foreach (var site in sites)
            if (!result.ContainsKey(site.Id)) result[site.Id] = new SitePlanningProfile(site.Id, site.ExternalCode, site.Name, null, InferRegion(site.CollectionAddress ?? site.Name), site.CollectionAddress);
        return result;
    }

    private static async Task SaveProfileAsync(TmsDbContext db, SitePlanningProfile profile, string note, CancellationToken ct)
    {
        var key = $"{ProfileType}:{profile.SiteId:N}";
        var row = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (row is null)
        {
            row = new StagedImport { EntityType = ProfileType, IdempotencyKey = key, PayloadJson = "{}", Source = "TMS site planning profile" };
            db.StagedImports.Add(row);
        }
        row.PayloadJson = JsonSerializer.Serialize(profile, JsonOptions);
        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = DateTimeOffset.UtcNow;
        row.ReviewedBy = "TMS";
        row.ReviewNote = note;
        await db.SaveChangesAsync(ct);
    }

    private static Site? MatchSite(IEnumerable<Site> sites, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var key = Normalise(value);
        return sites.FirstOrDefault(site => new[] { site.ExternalCode, site.Name, site.DriverTextName }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => Normalise(x) == key || key.Contains(Normalise(x)) || Normalise(x).Contains(key)));
    }

    private static decimal? ParseTemperature(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Where(c => char.IsDigit(c) || c is '-' or '+' or '.' or ',').ToArray()).Replace(',', '.');
        return decimal.TryParse(clean, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string InferRegion(string? value)
    {
        var text = (value ?? string.Empty).ToUpperInvariant();
        var token = text.Split(new[] { ' ', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(x => x.Any(char.IsDigit) && x.Any(char.IsLetter)) ?? string.Empty;
        var prefix = new string(token.TakeWhile(char.IsLetter).ToArray());
        if (new[] { "NE","NW","CA","DL","DH","LA","PR","FY","BB","BD","HD","HX","LS","YO","HG","HU","DN","S","SR","TS","WF" }.Contains(prefix)) return "North";
        if (new[] { "B","CV","DE","DY","LE","LN","NG","NN","ST","TF","WS","WV","WR" }.Contains(prefix)) return "Midlands";
        if (new[] { "CB","CM","CO","IP","NR","PE","SG","SS","AL","EN","IG","RM" }.Contains(prefix)) return "East";
        if (new[] { "BS","BA","GL","HR","NP","CF","LD","SA" }.Contains(prefix)) return "West / Wales";
        if (new[] { "BH","DT","EX","PL","TQ","TA","TR" }.Contains(prefix)) return "South West";
        if (new[] { "BN","CT","DA","GU","HA","HP","KT","ME","MK","OX","PO","RG","RH","SL","SM","SO","SP","TN","TW" }.Contains(prefix)) return "South East";
        if (new[] { "E","EC","N","SE","SW","W","WC" }.Contains(prefix)) return "London";
        return "Other";
    }

    private static string? Tagged(string? notes, string label)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        foreach (var segment in notes.Split(new[] { '·', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = segment.Trim();
            var prefix = $"{label}:";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return trimmed[prefix.Length..].Trim();
        }
        return null;
    }

    private static string? Text(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            foreach (var property in root.EnumerateObject())
                if (Normalise(property.Name) == Normalise(name))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? property.Value.ToString() : null;
        return null;
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool SchemaUnavailable(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record SitePlanningProfile(Guid SiteId, string ExternalCode, string Name, decimal? DefaultTemperatureC, string Region, string? Address);
public sealed record RunTemperatureContext(IReadOnlyDictionary<Guid, decimal?> OrderTemperatures, decimal? LoadTemperatureC, bool HasConflict, IReadOnlyList<decimal> DistinctTemperatures);
public sealed record TemperatureConflict(Guid LoadId, string LoadReference, IReadOnlyList<string> Temperatures);
public sealed record TemperatureSyncResult(int UpdatedLoads, int UpdatedOrders, IReadOnlyList<TemperatureConflict> Conflicts);
