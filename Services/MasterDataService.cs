using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class MasterDataService(TmsDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public Task<IReadOnlyList<MasterDriver>> GetActiveDriversAsync(CancellationToken ct = default) =>
        GetAsync("master:drivers", () => ReadOperationalDriversAsync(ct));

    public Task<IReadOnlyList<MasterVehicle>> GetActiveVehiclesAsync(CancellationToken ct = default) =>
        GetAsync("master:vehicles", async () => (await db.Vehicles.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Registration).ToListAsync(ct))
            .Select(x => new MasterVehicle
            {
                VehicleId = x.Id.ToString(), FleetNumber = x.FleetNumber, Registration = x.Registration,
                Abbreviation = x.Abbreviation, Transmission = x.Transmission, DvsCompliant = x.DvsCompliant,
                FuelProvider = x.FuelProvider, CabMobile = x.CabMobile, FuelPin = x.FuelPin,
                ShellCard = x.ShellCard, BpRedCard = x.BpRedCard, BpPlainCard = x.BpPlainCard,
                FuelPinSecretName = x.FuelPinSecretName, FuelCardLastFour = x.FuelCardLastFour,
                MOTExpiry = x.MOTExpiry?.ToDateTime(TimeOnly.MinValue),
                TachoCalibrationExpiry = x.TachoCalibrationExpiry?.ToDateTime(TimeOnly.MinValue),
                VehicleTestExpiry = x.VehicleTestExpiry?.ToDateTime(TimeOnly.MinValue),
                FleetioAssetId = x.FleetioId, FleetioName = x.FleetioName, FleetioStatus = x.FleetioStatus,
                Notes = x.Notes, IsActive = x.Active
            }).ToList());

    public Task<IReadOnlyList<MasterTrailer>> GetActiveTrailersAsync(CancellationToken ct = default) =>
        GetAsync("master:trailers", async () => (await db.Trailers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.TrailerNumber).ToListAsync(ct))
            .Select(x => new MasterTrailer
            {
                TrailerId = x.Id.ToString(), TrailerNumber = x.TrailerNumber, Registration = x.TrailerNumber,
                TrailerType = x.Type, StandardCapacity = x.StandardCapacity, EuroCapacity = x.EuroCapacity,
                IsActive = x.Active
            }).ToList());

    public Task<IReadOnlyList<MasterDepot>> GetActiveDepotsAsync(CancellationToken ct = default) =>
        GetAsync("master:depots", () => db.MasterDepots.AsNoTracking().OrderBy(x => x.DepotName).ToListAsync(ct));

    public Task<IReadOnlyList<MasterCustomer>> GetActiveCustomersAsync(CancellationToken ct = default) =>
        GetAsync("master:customers", async () => (await db.Customers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct))
            .Select(x => new MasterCustomer
            {
                CustomerId = x.Code, CustomerName = x.Name, AccountCode = x.Code,
                TradingName = x.TradingName, AccountOwner = x.AccountOwner,
                ServiceNotes = x.ServiceNotes, DefaultSiteCode = x.DefaultSiteCode,
                IsActive = x.Active
            }).ToList());

    public Task<IReadOnlyList<MasterSite>> GetActiveSitesAsync(CancellationToken ct = default) =>
        GetAsync("master:sites", () => ReadOperationalSitesAsync(ct));

    public Task<IReadOnlyList<MasterSubcontractor>> GetActiveSubcontractorsAsync(CancellationToken ct = default) =>
        GetAsync("master:subcontractors", () => db.MasterSubcontractors.AsNoTracking().OrderBy(x => x.CompanyName).ToListAsync(ct));

    public Task<IReadOnlyList<MasterMarket>> GetActiveMarketsAsync(CancellationToken ct = default) =>
        GetAsync("master:markets", async () => (await db.MarketContacts.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Market).ThenBy(x => x.Name).ThenBy(x => x.StandOrLocation).ToListAsync(ct))
            .Select(x => new MasterMarket
            {
                MarketId = x.MarketKey ?? x.Id.ToString("N"), Market = x.Market, Name = x.Name,
                StandOrLocation = x.StandOrLocation, Salesman = x.Salesman, Sender = x.Sender,
                IsActive = x.Active
            }).ToList());

    public Task<IReadOnlyList<MasterFuelCard>> GetActiveFuelCardsAsync(CancellationToken ct = default) =>
        GetAsync("master:fuel-cards", async () => (await db.Vehicles.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Registration).ToListAsync(ct))
            .Select(x => new MasterFuelCard
            {
                FuelCardId = x.FleetNumber ?? x.Registration, VehicleId = x.Id.ToString(), Registration = x.Registration,
                FuelProvider = x.FuelProvider, FuelPinSecretName = x.FuelPinSecretName,
                FuelCardLastFour = x.FuelCardLastFour, ShellCard = x.ShellCard,
                BpRedCard = x.BpRedCard, BpPlainCard = x.BpPlainCard, IsActive = x.Active
            }).ToList());

    public Task<IReadOnlyList<MasterFuelPrice>> GetActiveFuelPricesAsync(CancellationToken ct = default) =>
        GetAsync("master:fuel-prices", async () => (await db.FuelPrices.AsNoTracking().OrderByDescending(x => x.WeekCommencing).ThenBy(x => x.Provider).ToListAsync(ct))
            .Select(x => new MasterFuelPrice
            {
                FuelPriceId = x.Id.ToString(), WeekCommencing = x.WeekCommencing.ToDateTime(TimeOnly.MinValue),
                Provider = x.Provider, PricePencePerLitre = x.PricePencePerLitre,
                IsPricingMaximum = x.IsPricingMaximum, Source = x.Source, Notes = x.Notes, IsActive = true
            }).ToList());

    public Task<MasterSite?> GetSiteByIdAsync(string siteId, CancellationToken ct = default) =>
        ReadOperationalSiteByKeyAsync(siteId, ct);

    public Task<MasterDriver?> GetDriverByIdAsync(string driverId, CancellationToken ct = default) =>
        ReadOperationalDriverByKeyAsync(driverId, ct);

    public void Invalidate()
    {
        foreach (var key in CacheKeys)
            cache.Remove(key);
    }

    private static readonly string[] CacheKeys =
    [
        "master:drivers", "master:vehicles", "master:trailers", "master:depots",
        "master:customers", "master:sites", "master:subcontractors", "master:markets",
        "master:fuel-cards", "master:fuel-prices"
    ];

    private async Task<IReadOnlyList<T>> GetAsync<T>(string key, Func<Task<List<T>>> factory)
    {
        if (cache.TryGetValue(key, out IReadOnlyList<T>? cached) && cached is not null)
            return cached;

        var rows = await factory();
        cache.Set(key, (IReadOnlyList<T>)rows, CacheDuration);
        return rows;
    }

    private async Task<List<MasterDriver>> ReadOperationalDriversAsync(CancellationToken ct)
    {
        var drivers = await db.Drivers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        return drivers.Where(DriverPopulationRules.IsDriver)
            .GroupBy(x => !string.IsNullOrWhiteSpace(x.TachoMasterDriverId) ? $"member:{x.TachoMasterDriverId}" :
                          !string.IsNullOrWhiteSpace(x.TachoCardNumber) ? $"card:{x.TachoCardNumber}" : $"employee:{x.EmployeeNumber}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(x => x.DisplayName)
            .Select(x => new MasterDriver
            {
                DriverId = x.TachoMasterDriverId ?? x.EmployeeNumber, FullName = x.DisplayName,
                DisplayName = x.DisplayName, TachoName = x.TachoName, MobileNumber = x.MobileNumber,
                TachoCardNumber = x.TachoCardNumber, TachoMasterDriverId = x.TachoMasterDriverId,
                DriverType = x.DriverType, DriverGroup = x.DriverGroup, Skills = x.Skills,
                Coding = x.Coding, AgencyName = x.AgencyName, Notes = x.Notes,
                LicenceNumber = x.DrivingLicenceNumber, LicenceExpiry = x.LicenceExpiry?.ToDateTime(TimeOnly.MinValue),
                CPCExpiry = x.CPCExpiry?.ToDateTime(TimeOnly.MinValue),
                DigitalTachoCardExpiry = x.DigitalTachoCardExpiry?.ToDateTime(TimeOnly.MinValue),
                MedicalExpiry = x.MedicalExpiry?.ToDateTime(TimeOnly.MinValue),
                IsActive = x.Active
            }).ToList();
    }

    private async Task<List<MasterSite>> ReadOperationalSitesAsync(CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        return sites.Select(x => new MasterSite
        {
            SiteId = x.ExternalCode, SiteName = x.Name, CustomerCode = x.CustomerCode,
            Address = x.CollectionAddress, Latitude = x.Latitude, Longitude = x.Longitude,
            DriverTextName = x.DriverTextName, CollectionInstructions = x.CollectionInstructions,
            MapLink = x.MapLink, OperationalRegion = x.OperationalRegion, IsActive = x.Active
        }).ToList();
    }

    private async Task<MasterSite?> ReadOperationalSiteByKeyAsync(string siteId, CancellationToken ct)
    {
        var siteQuery = db.Sites.AsNoTracking().Where(x => x.Active && x.ExternalCode == siteId);
        if (Guid.TryParse(siteId, out var siteGuid)) siteQuery = siteQuery.Union(db.Sites.AsNoTracking().Where(x => x.Active && x.Id == siteGuid));
        var site = await siteQuery.SingleOrDefaultAsync(ct);
        if (site is null) return null;
        await MasterDetailStore.EnrichSitesAsync(db, [site], ct);
        return new MasterSite
        {
            SiteId = site.ExternalCode, SiteName = site.Name, CustomerCode = site.CustomerCode,
            Address = site.CollectionAddress, Latitude = site.Latitude, Longitude = site.Longitude,
            DriverTextName = site.DriverTextName, CollectionInstructions = site.CollectionInstructions,
            MapLink = site.MapLink, OperationalRegion = site.OperationalRegion, IsActive = site.Active
        };
    }

    private async Task<MasterDriver?> ReadOperationalDriverByKeyAsync(string driverId, CancellationToken ct)
    {
        var driverQuery = db.Drivers.AsNoTracking().Where(x => x.Active &&
            (x.TachoMasterDriverId == driverId || x.EmployeeNumber == driverId));
        if (Guid.TryParse(driverId, out var driverGuid)) driverQuery = driverQuery.Union(db.Drivers.AsNoTracking().Where(x => x.Active && x.Id == driverGuid));
        var drivers = await driverQuery.ToListAsync(ct);
        if (drivers.Count == 0) return null;
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var x = drivers.OrderByDescending(item => item.LastTachoSyncUtc).First();
        if (!DriverPopulationRules.IsDriver(x)) return null;
        return new MasterDriver
        {
            DriverId = x.TachoMasterDriverId ?? x.EmployeeNumber, FullName = x.DisplayName,
            DisplayName = x.DisplayName, TachoName = x.TachoName, MobileNumber = x.MobileNumber,
            TachoCardNumber = x.TachoCardNumber, TachoMasterDriverId = x.TachoMasterDriverId,
            DriverType = x.DriverType, DriverGroup = x.DriverGroup, Skills = x.Skills,
            Coding = x.Coding, AgencyName = x.AgencyName, Notes = x.Notes,
            LicenceNumber = x.DrivingLicenceNumber, LicenceExpiry = x.LicenceExpiry?.ToDateTime(TimeOnly.MinValue),
            CPCExpiry = x.CPCExpiry?.ToDateTime(TimeOnly.MinValue),
            DigitalTachoCardExpiry = x.DigitalTachoCardExpiry?.ToDateTime(TimeOnly.MinValue),
            MedicalExpiry = x.MedicalExpiry?.ToDateTime(TimeOnly.MinValue),
            IsActive = x.Active
        };
    }
}
