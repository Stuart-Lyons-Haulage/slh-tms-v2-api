using Microsoft.EntityFrameworkCore;

namespace Slh.Tms.Api.Models;

public abstract class ActiveMasterRow
{
    public int SharePointItemId { get; set; }
    public DateTime LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsActive { get; set; }
}

public sealed class MasterDepot : ActiveMasterRow
{
    public string DepotId { get; set; } = "";
    public string DepotName { get; set; } = "";
    public string? Address { get; set; }
    public string? Postcode { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public int? GeofenceRadiusMetres { get; set; }
}

public sealed class MasterCustomer : ActiveMasterRow
{
    public string CustomerId { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string? AccountCode { get; set; }
    public string? TradingName { get; set; }
    public string? CustomerAliases { get; set; }
    public string? InvoiceAddress { get; set; }
    public string? InvoiceEmail { get; set; }
    public string? DefaultContactName { get; set; }
    public string? DefaultContactPhone { get; set; }
    public string? AccountOwner { get; set; }
    public string? ServiceNotes { get; set; }
    public string? DefaultSiteCode { get; set; }
}

public sealed class MasterCustomerContact : ActiveMasterRow
{
    public string ContactId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string? Email { get; set; }
    public string? MobileNumber { get; set; }
    public bool ReceivesEtaUpdates { get; set; }
}

public sealed class MasterDriver : ActiveMasterRow
{
    public string DriverId { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? PreferredName { get; set; }
    public string? DisplayName { get; set; }
    public string? TachoName { get; set; }
    public string? MobileNumber { get; set; }
    public string? LicenceNumber { get; set; }
    public DateTime? LicenceExpiry { get; set; }
    public DateTime? CPCExpiry { get; set; }
    public DateTime? DigitalTachoCardExpiry { get; set; }
    public DateTime? MedicalExpiry { get; set; }
    public string? TachoCardNumber { get; set; }
    public string? TachoMasterDriverId { get; set; }
    public string? EmploymentType { get; set; }
    public string? AgencyName { get; set; }
    public string? DriverType { get; set; }
    public string? DriverGroup { get; set; }
    public string? Skills { get; set; }
    public string? Coding { get; set; }
    public string? Notes { get; set; }
    public string? DefaultDepotId { get; set; }
}

public sealed class MasterVehicle : ActiveMasterRow
{
    public string VehicleId { get; set; } = "";
    public string? FleetNumber { get; set; }
    public string Registration { get; set; } = "";
    public string? VehicleType { get; set; }
    public string? Abbreviation { get; set; }
    public string? Transmission { get; set; }
    public bool? DvsCompliant { get; set; }
    public string? FuelProvider { get; set; }
    public string? CabMobile { get; set; }
    public string? FuelPin { get; set; }
    public string? ShellCard { get; set; }
    public string? BpRedCard { get; set; }
    public string? BpPlainCard { get; set; }
    public string? FuelPinSecretName { get; set; }
    public string? FuelCardLastFour { get; set; }
    public string? FleetioAssetId { get; set; }
    public string? FleetioName { get; set; }
    public string? FleetioStatus { get; set; }
    public string? SamsaraAssetId { get; set; }
    public DateTime? MOTExpiry { get; set; }
    public DateTime? TachoCalibrationExpiry { get; set; }
    public DateTime? VehicleTestExpiry { get; set; }
    public string? Notes { get; set; }
    public string? DefaultDepotId { get; set; }
}

public sealed class MasterTrailer : ActiveMasterRow
{
    public string TrailerId { get; set; } = "";
    public string? FleetNumber { get; set; }
    public string? Registration { get; set; }
    public string? TrailerNumber { get; set; }
    public string? TrailerType { get; set; }
    public int? StandardCapacity { get; set; }
    public int? EuroCapacity { get; set; }
    public DateTime? MOTExpiry { get; set; }
    public DateTime? TestExpiry { get; set; }
    public string? DefaultDepotId { get; set; }
}

public sealed class MasterSite : ActiveMasterRow
{
    public string SiteId { get; set; } = "";
    public string SiteName { get; set; } = "";
    public string? CustomerId { get; set; }
    public string? CustomerCode { get; set; }
    public string? Address { get; set; }
    public string? Address2 { get; set; }
    public string? Postcode { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public int? GeofenceRadiusMetres { get; set; }
    public string? SiteType { get; set; }
    public TimeSpan? OpenTime { get; set; }
    public TimeSpan? CloseTime { get; set; }
    public string? SpecialInstructions { get; set; }
    public string? DriverTextName { get; set; }
    public string? CollectionInstructions { get; set; }
    public string? MapLink { get; set; }
    public string? OperationalRegion { get; set; }
}

public sealed class MasterSubcontractor : ActiveMasterRow
{
    public string SubcontractorId { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
    public string? OperatorLicenceNumber { get; set; }
    public DateTime? OperatorLicenceExpiry { get; set; }
    public DateTime? InsuranceExpiry { get; set; }
}

public sealed class MasterMarket : ActiveMasterRow
{
    public string MarketId { get; set; } = "";
    public string Market { get; set; } = "";
    public string Name { get; set; } = "";
    public string? StandOrLocation { get; set; }
    public string? Salesman { get; set; }
    public string? Sender { get; set; }
}

public sealed class MasterFuelCard : ActiveMasterRow
{
    public string FuelCardId { get; set; } = "";
    public string? VehicleId { get; set; }
    public string? Registration { get; set; }
    public string? FuelProvider { get; set; }
    public string? FuelPinSecretName { get; set; }
    public string? FuelCardLastFour { get; set; }
    public string? ShellCard { get; set; }
    public string? BpRedCard { get; set; }
    public string? BpPlainCard { get; set; }
}

public sealed class MasterFuelPrice : ActiveMasterRow
{
    public string FuelPriceId { get; set; } = "";
    public DateTime WeekCommencing { get; set; }
    public string Provider { get; set; } = "";
    public decimal PricePencePerLitre { get; set; }
    public bool IsPricingMaximum { get; set; }
    public string? Source { get; set; }
    public string? Notes { get; set; }
}