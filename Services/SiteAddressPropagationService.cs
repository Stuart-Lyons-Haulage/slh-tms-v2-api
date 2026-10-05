using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record SiteAddressPropagationResult(bool Updated, string? SiteCode, string? SiteName, int OrdersUpdated, string Message);

/// <summary>
/// Keeps the canonical Site address and open operational orders aligned without
/// rewriting delivered history or guessing between ambiguous sites.
/// </summary>
public sealed class SiteAddressPropagationService(TmsDbContext db)
{
    public async Task<SiteAddressPropagationResult> UpdateMasterFromOrderAsync(string? siteLabel, string? address, string? mapLink, string? actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(siteLabel) || string.IsNullOrWhiteSpace(address))
            return new(false, null, null, 0, "The order was saved, but no unambiguous delivery Site Master record was identified.");

        var sites = await db.Sites.Where(site => site.Active).Take(5000).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        var resolution = SiteMasterIdentityResolver.Resolve(new IncomingSiteIdentity(siteLabel, siteLabel, null, null, null, null), sites);
        if (!resolution.Matched)
            return new(false, null, null, 0, "The order was saved, but its delivery location did not match exactly one active Site Master record.");

        var site = resolution.Site!;
        var before = JsonSerializer.Serialize(site);
        var nextAddress = address.Trim();
        var changed = !string.Equals(site.CollectionAddress?.Trim(), nextAddress, StringComparison.OrdinalIgnoreCase);
        if (changed)
        {
            site.CollectionAddress = nextAddress;
            site.MapLink = string.IsNullOrWhiteSpace(mapLink) ? $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(nextAddress)}" : mapLink.Trim();
            await MasterDetailStore.SaveAsync(db, "site", site.ExternalCode, JsonSerializer.Serialize(site), "Approved order address amendment", actor, ct);
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Site",
                EntityId = site.Id,
                Action = "AddressUpdatedFromApprovedOrder",
                ChangesJson = JsonSerializer.Serialize(new { before = JsonDocument.Parse(before).RootElement, after = JsonDocument.Parse(JsonSerializer.Serialize(site)).RootElement }),
                ChangedBy = actor ?? "system:approved-order"
            });
            await db.SaveChangesAsync(ct);
        }

        return new(changed, site.ExternalCode, site.Name, 0, changed ? $"Site Master updated for {site.ExternalCode} · {site.Name}." : $"Site Master already contains this address for {site.ExternalCode} · {site.Name}.");
    }

    public async Task<int> PropagateMasterAddressToOpenOrdersAsync(Site site, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(site.CollectionAddress)) return 0;
        var orders = await db.TransportOrders.Where(order => order.Status == OrderStatus.Draft || order.Status == OrderStatus.ReadyToPlan || order.Status == OrderStatus.Planned).ToListAsync(ct);
        var matchingOrders = orders.Where(order => MatchesDeliverySite(order, site)).ToList();
        if (matchingOrders.Count == 0) return 0;
        var orderIds = matchingOrders.Select(order => order.Id).ToList();
        var stops = await db.LoadStops.Where(stop => stop.OrderId.HasValue && orderIds.Contains(stop.OrderId.Value)).ToListAsync(ct);
        var address = site.CollectionAddress.Trim();
        var mapLink = site.MapLink;
        foreach (var order in matchingOrders)
        {
            order.DriverInstructions = ReplaceTag(order.DriverInstructions, "Delivery address", address);
            order.MapLink = string.IsNullOrWhiteSpace(mapLink) ? $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(address)}" : mapLink;
        }
        foreach (var stop in stops)
        {
            stop.Address = address;
            stop.Latitude = null;
            stop.Longitude = null;
        }
        await db.SaveChangesAsync(ct);
        return matchingOrders.Count;
    }

    private static bool MatchesDeliverySite(TransportOrder order, Site site) => new[] { order.MarketName, order.StallNumber }.Where(value => !string.IsNullOrWhiteSpace(value)).Any(value => Matches(value!, site));

    private static bool Matches(string value, Site site)
    {
        var target = SiteMasterIdentityResolver.Normalise(value);
        return new[] { site.ExternalCode, site.Name, site.DriverTextName }
            .Concat((site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(SiteMasterIdentityResolver.Normalise)
            .Any(item => item == target);
    }

    private static string ReplaceTag(string? notes, string label, string value)
    {
        var parts = (notes ?? string.Empty).Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var prefix = $"{label}:";
        var index = parts.FindIndex(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) parts[index] = $"{label}: {value}"; else parts.Add($"{label}: {value}");
        var result = string.Join(" · ", parts);
        return result.Length <= 1000 ? result : result[..1000];
    }
}
