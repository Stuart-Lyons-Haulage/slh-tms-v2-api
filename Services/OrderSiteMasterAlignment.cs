using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class OrderSiteMasterAlignment
{
    private static readonly TimeSpan SiteCacheDuration = TimeSpan.FromSeconds(45);
    private static readonly SemaphoreSlim SiteCacheLock = new(1, 1);
    private static readonly Dictionary<string, SiteCacheEntry> SiteCaches = new(StringComparer.Ordinal);

    public sealed record Alignment(
        string? CollectionName,
        string? CollectionAddress,
        string? DeliveryName,
        string? DeliveryAddress,
        string? DeliveryMapLink,
        string? DriverInstructions,
        string? MarketCustomer = null,
        string? MarketStand = null,
        string? MarketSalesman = null,
        bool CollectionMatched = false,
        bool DeliveryMatched = false);

    private sealed record MarketContext(string Market, string Customer, string? Stand, string? Salesman);
    private sealed record SiteCacheEntry(DateTimeOffset ExpiresAtUtc, IReadOnlyList<Site> Sites);

    public static async Task<Alignment> ResolveAsync(TmsDbContext db, JsonElement payload, CancellationToken ct)
    {
        var rawCollection = Text(payload, "collectionSite") ?? Text(payload, "collectionLocation") ?? Text(payload, "sellerName");
        var rawDelivery = Text(payload, "deliverySite") ?? Text(payload, "deliveryLocation") ?? Text(payload, "stallNumber") ?? Text(payload, "destination");
        return await ResolveNamesAsync(
            db,
            rawCollection,
            rawDelivery,
            Text(payload, "collectionAddress"),
            Text(payload, "deliveryAddress"),
            Text(payload, "mapLink"),
            Text(payload, "driverInstructions"),
            ct,
            Text(payload, "marketName"));
    }

    public static async Task<Alignment> ResolveNamesAsync(
        TmsDbContext db,
        string? rawCollection,
        string? rawDelivery,
        string? rawCollectionAddress,
        string? rawDeliveryAddress,
        string? rawMapLink,
        string? rawDriverInstructions,
        CancellationToken ct,
        string? marketName = null)
    {
        List<Site> sites;
        try
        {
            sites = await LoadActiveSitesAsync(db, ct);
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            return new Alignment(rawCollection, rawCollectionAddress, rawDelivery, rawDeliveryAddress, rawMapLink, rawDriverInstructions);
        }

        var collection = Match(sites, rawCollection);
        var marketContext = await MatchMarketContextAsync(db, marketName, rawDelivery, ct);

        // The market is the physical delivery location and therefore owns the geofence.
        // Resolve it independently of the trader/stall lookup so a missing or ambiguous
        // Market Master contact can never turn a market delivery into a fake site.
        var marketSite = Match(sites, marketName)
            ?? (marketContext is null ? null : Match(sites, marketContext.Market));
        var delivery = marketSite ?? Match(sites, rawDelivery);

        var collectionName = DisplayName(collection) ?? rawCollection;
        // A market customer/stall is not a physical geofence. When Site Master resolves
        // the market, keep the stop at that Market Site and carry its internal destination
        // from Markets Master in the driver instructions and explicit market fields.
        var deliveryName = DisplayName(delivery) ?? (marketContext?.Market ?? rawDelivery);
        var collectionAddress = collection?.CollectionAddress ?? rawCollectionAddress;
        var deliveryAddress = delivery?.CollectionAddress ?? rawDeliveryAddress;
        var deliveryMapLink = delivery?.MapLink ?? rawMapLink;

        var instructions = rawDriverInstructions;
        instructions = UpsertTag(instructions, "Collection site", collectionName);
        instructions = UpsertTag(instructions, "Collection address", collectionAddress);
        instructions = UpsertTag(instructions, "Depot", deliveryName);
        instructions = UpsertTag(instructions, "Delivery address", deliveryAddress);
        if (marketContext is not null)
        {
            instructions = UpsertTag(instructions, "Market", deliveryName ?? marketContext.Market);
            instructions = UpsertTag(instructions, "Market customer", marketContext.Customer);
            instructions = UpsertTag(instructions, "Stall / stand", marketContext.Stand);
            instructions = UpsertTag(instructions, "Salesman", marketContext.Salesman);
        }

        return new Alignment(
            collectionName,
            collectionAddress,
            deliveryName,
            deliveryAddress,
            deliveryMapLink,
            instructions,
            marketContext?.Customer,
            marketContext?.Stand,
            marketContext?.Salesman,
            collection is not null,
            delivery is not null);
    }

    private static async Task<List<Site>> LoadActiveSitesAsync(TmsDbContext db, CancellationToken ct)
    {
        var cacheKey = SiteCacheKey(db);
        var now = DateTimeOffset.UtcNow;
        if (SiteCaches.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > now)
            return CloneSites(cached.Sites);

        await SiteCacheLock.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (SiteCaches.TryGetValue(cacheKey, out cached) && cached.ExpiresAtUtc > now)
                return CloneSites(cached.Sites);

            var sites = await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
            SiteCaches[cacheKey] = new SiteCacheEntry(now.Add(SiteCacheDuration), CloneSites(sites));
            return CloneSites(SiteCaches[cacheKey].Sites);
        }
        finally
        {
            SiteCacheLock.Release();
        }
    }

    private static string SiteCacheKey(TmsDbContext db)
    {
        if (db.Database.IsRelational())
            return $"relational:{db.Database.GetConnectionString() ?? db.Database.ProviderName ?? "unknown"}";

        // Non-relational providers are mostly test hosts. Do not share cache across isolated
        // in-memory contexts because that can make one test/order resolve against another
        // database's temporary sites.
        return $"context:{db.ContextId.InstanceId}";
    }

    private static List<Site> CloneSites(IEnumerable<Site> sites) => sites.Select(CloneSite).ToList();

    private static Site CloneSite(Site site) => new()
    {
        Id = site.Id,
        ExternalCode = site.ExternalCode,
        CustomerCode = site.CustomerCode,
        Name = site.Name,
        DriverTextName = site.DriverTextName,
        CollectionAddress = site.CollectionAddress,
        CollectionInstructions = site.CollectionInstructions,
        MapLink = site.MapLink,
        Latitude = site.Latitude,
        Longitude = site.Longitude,
        Aliases = site.Aliases,
        CustomField1 = site.CustomField1,
        CustomField2 = site.CustomField2,
        CustomField3 = site.CustomField3,
        OperationalRegion = site.OperationalRegion,
        Active = site.Active
    };

    private static async Task<MarketContext?> MatchMarketContextAsync(TmsDbContext db, string? marketName, string? destination, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(marketName) || string.IsNullOrWhiteSpace(destination)) return null;
        var market = CanonicalMarket(marketName);
        var destinationKey = Normalise(destination);
        var contacts = await db.MarketContacts.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var sameMarket = contacts.Where(x => CanonicalMarket(x.Market) == market).ToList();
        if (sameMarket.Count == 0) return null;

        static IEnumerable<string?> Evidence(MarketContact contact)
        {
            yield return contact.Name;
            yield return contact.StandOrLocation;
            yield return contact.Salesman;
            yield return contact.Sender;
        }

        var exact = sameMarket.Where(contact => Evidence(contact).Any(value => Normalise(value) == destinationKey)).ToList();
        var candidates = exact.Count > 0 ? exact : sameMarket.Where(contact => Evidence(contact).Any(value =>
        {
            var key = Normalise(value);
            return key.Length >= 4 && destinationKey.Length >= 4 &&
                (key.Contains(destinationKey, StringComparison.Ordinal) || destinationKey.Contains(key, StringComparison.Ordinal));
        })).ToList();

        var unique = candidates.GroupBy(x => x.Id).Select(x => x.First()).ToList();
        if (unique.Count != 1) return null;
        var match = unique[0];
        return new MarketContext(match.Market.Trim(), match.Name.Trim(), Clean(match.StandOrLocation), Clean(match.Salesman));
    }

    private static Site? Match(IEnumerable<Site> sites, string? value)
    {
        var key = Normalise(value);
        if (string.IsNullOrWhiteSpace(key)) return null;
        var exact = sites.Where(site => Candidates(site).Any(candidate => Normalise(candidate) == key)).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        var partial = sites.Where(site => Candidates(site).Any(candidate =>
        {
            var candidateKey = Normalise(candidate);
            return candidateKey.Length >= 5 && key.Length >= 5 &&
                (key.Contains(candidateKey, StringComparison.Ordinal) || candidateKey.Contains(key, StringComparison.Ordinal));
        })).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    private static IEnumerable<string?> Candidates(Site site)
    {
        yield return site.ExternalCode;
        yield return site.Name;
        yield return site.DriverTextName;
        foreach (var alias in (site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return alias;
    }

    private static string? DisplayName(Site? site) => site is null ? null :
        !string.IsNullOrWhiteSpace(site.DriverTextName) ? site.DriverTextName.Trim() : site.Name.Trim();

    private static string? UpsertTag(string? notes, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return notes;
        var parts = (notes ?? string.Empty).Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var prefix = $"{label}:";
        var index = parts.FindIndex(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var tagged = $"{label}: {value.Trim()}";
        if (index >= 0) parts[index] = tagged;
        else parts.Add(tagged);
        return string.Join(" · ", parts);
    }

    private static string? Text(JsonElement payload, string name)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (Normalise(property.Name) != Normalise(name)) continue;
            return property.Value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(property.Value.GetString()) ? null : property.Value.GetString()!.Trim(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.ToString(),
                _ => null
            };
        }
        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string CanonicalMarket(string? value)
    {
        var normal = Normalise(value);
        if (normal.Contains("COVENT")) return "COVENT";
        if (normal.Contains("SPIT")) return "SPIT";
        if (normal.Contains("WESTERN")) return "WESTERN";
        if (normal.Contains("SENDER")) return "SENDER";
        return normal;
    }

    private static string Normalise(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool SchemaUnavailable(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}
