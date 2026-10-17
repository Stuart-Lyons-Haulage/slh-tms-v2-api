using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// One-time bulk import of master data from CSV exports.
/// Used to populate Vehicles, Trailers, Sites, Fuel Cards, and Fuel Prices from external sources.
/// </summary>
public sealed class CsvMasterDataImportService(TmsDbContext db)
{
    public async Task<ImportResult> ImportVehiclesAsync(Stream csvStream, CancellationToken ct = default)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true };
        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, config);

        var records = csv.GetRecords<VehicleImportDto>().ToList();
        var result = new ImportResult { TableName = "Vehicles", RequestedRowCount = records.Count };

        foreach (var record in records)
        {
            try
            {
                var existing = await db.Vehicles.FirstOrDefaultAsync(x => x.Registration == record.Registration, ct);
                if (existing != null)
                {
                    result.SkippedRows++;
                    continue;
                }

                var vehicle = new Vehicle
                {
                    Id = record.Id ?? Guid.NewGuid(),
                    Registration = record.Registration ?? "",
                    FleetNumber = record.FleetNumber,
                    Abbreviation = record.Abbreviation,
                    VehicleType = record.VehicleType,
                    CabMobile = record.CabMobile,
                    FuelProvider = record.FuelProvider,
                    FuelPin = record.FuelPin,
                    ShellCard = record.ShellCard,
                    BpRedCard = record.BpRedCard,
                    BpPlainCard = record.BpPlainCard,
                    FleetioId = record.FleetioId,
                    MOTExpiry = ParseDate(record.MotExpiry),
                    TachoCalibrationExpiry = ParseDate(record.TachoCalibrationExpiry),
                    Active = record.Active,
                    VIN = record.Vin,
                    OwnerType = record.OwnerType,
                    VehicleSite = record.VehicleSite,
                    Transmission = record.Transmission,
                    DvsCompliant = record.DvsCompliant,
                    FuelPinSecretName = record.FuelPinSecretName,
                    FuelCardLastFour = record.FuelCardLastFour,
                    VehicleTestExpiry = ParseDate(record.VehicleTestExpiry),
                    Notes = record.Notes,
                    FleetioName = record.FleetioName,
                    FleetioStatus = record.FleetioStatus
                };

                db.Vehicles.Add(vehicle);
                result.InsertedRows++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Row {record.Registration}: {ex.Message}");
            }
        }

        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<ImportResult> ImportTrailersAsync(Stream csvStream, CancellationToken ct = default)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true };
        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, config);

        var records = csv.GetRecords<TrailerImportDto>().ToList();
        var result = new ImportResult { TableName = "Trailers", RequestedRowCount = records.Count };

        foreach (var record in records)
        {
            try
            {
                var existing = await db.Trailers.FirstOrDefaultAsync(x => x.TrailerNumber == record.TrailerNumber, ct);
                if (existing != null)
                {
                    result.SkippedRows++;
                    continue;
                }

                var trailer = new Trailer
                {
                    Id = record.Id ?? Guid.NewGuid(),
                    TrailerNumber = record.TrailerNumber ?? "",
                    Type = record.Type,
                    StandardCapacity = ParseInt(record.StandardCapacity),
                    EuroCapacity = ParseInt(record.EuroCapacity),
                    Active = record.Active,
                    FleetioId = record.FleetioId,
                    FleetioName = record.FleetioName,
                    FleetioStatus = record.FleetioStatus,
                    Notes = record.Notes
                };

                db.Trailers.Add(trailer);
                result.InsertedRows++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Row {record.TrailerNumber}: {ex.Message}");
            }
        }

        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<ImportResult> ImportSitesAsync(Stream csvStream, CancellationToken ct = default)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true };
        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, config);

        var records = csv.GetRecords<SiteImportDto>().ToList();
        var result = new ImportResult { TableName = "Sites", RequestedRowCount = records.Count };

        foreach (var record in records)
        {
            try
            {
                var existing = await db.Sites.FirstOrDefaultAsync(x => x.ExternalCode == record.ExternalCode, ct);
                if (existing != null)
                {
                    result.SkippedRows++;
                    continue;
                }

                var site = new Site
                {
                    Id = record.Id ?? Guid.NewGuid(),
                    ExternalCode = record.ExternalCode ?? "",
                    Name = record.Name ?? "",
                    DriverTextName = record.DriverTextName,
                    CollectionAddress = record.CollectionAddress,
                    CollectionInstructions = record.CollectionInstructions,
                    MapLink = record.MapLink,
                    Latitude = ParseDecimal(record.Latitude),
                    Longitude = ParseDecimal(record.Longitude),
                    OperationalRegion = record.OperationalRegion,
                    Active = record.Active,
                    CustomerCode = record.CustomerCode,
                    GeofenceRadiusMetres = ParseInt(record.GeofenceRadiusMetres),
                    CustomField1 = record.CustomField1,
                    CustomField2 = record.CustomField2,
                    CustomField3 = record.CustomField3,
                    DefaultTemperatureC = ParseInt(record.DefaultTemperatureC),
                    Region = record.Region,
                    PlanningProfileSource = record.PlanningProfileSource,
                    AddressCompleteness = record.AddressCompleteness
                };

                db.Sites.Add(site);
                result.InsertedRows++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Row {record.ExternalCode}: {ex.Message}");
            }
        }

        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<ImportResult> ImportFuelCardsAsync(Stream csvStream, CancellationToken ct = default)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true };
        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, config);

        var records = csv.GetRecords<FuelCardImportDto>().ToList();
        var result = new ImportResult { TableName = "FuelCards", RequestedRowCount = records.Count };

        foreach (var record in records)
        {
            try
            {
                var existingVehicle = await db.Vehicles.FirstOrDefaultAsync(x => x.Registration == record.Registration, ct);
                if (existingVehicle == null)
                {
                    // Create minimal vehicle record for fuel card
                    existingVehicle = new Vehicle
                    {
                        Id = Guid.NewGuid(),
                        Registration = record.Registration ?? "",
                        Active = record.Active,
                        FuelProvider = record.FuelProvider,
                        FuelPin = record.FuelPin,
                        ShellCard = record.ShellCard,
                        BpRedCard = record.BpRedCard,
                        BpPlainCard = record.BpPlainCard,
                        FuelPinSecretName = record.FuelPinSecretName,
                        FuelCardLastFour = record.FuelCardLastFour
                    };
                    db.Vehicles.Add(existingVehicle);
                }

                result.InsertedRows++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Row {record.Registration}: {ex.Message}");
            }
        }

        await db.SaveChangesAsync(ct);
        return result;
    }

    private static DateOnly? ParseDate(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return null;
        if (DateOnly.TryParse(dateStr, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            return date;
        return null;
    }

    private static decimal? ParseDecimal(string? numStr)
    {
        if (string.IsNullOrWhiteSpace(numStr)) return null;
        if (decimal.TryParse(numStr, CultureInfo.InvariantCulture, out var num))
            return num;
        return null;
    }

    private static int? ParseInt(string? numStr)
    {
        if (string.IsNullOrWhiteSpace(numStr)) return null;
        if (int.TryParse(numStr, CultureInfo.InvariantCulture, out var num))
            return num;
        return null;
    }
}

public sealed class ImportResult
{
    public string TableName { get; set; } = "";
    public int RequestedRowCount { get; set; }
    public int InsertedRows { get; set; }
    public int SkippedRows { get; set; }
    public List<string> Errors { get; set; } = [];
}

#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.

public sealed class VehicleImportDto
{
    public Guid? Id { get; set; }
    public string Registration { get; set; }
    public string FleetNumber { get; set; }
    public string Abbreviation { get; set; }
    public string VehicleType { get; set; }
    public string CabMobile { get; set; }
    public string FuelProvider { get; set; }
    public string FuelPin { get; set; }
    public string ShellCard { get; set; }
    public string BpRedCard { get; set; }
    public string BpPlainCard { get; set; }
    public int? FleetioId { get; set; }
    public string MotExpiry { get; set; }
    public string TachoCalibrationExpiry { get; set; }
    public bool Active { get; set; }
    public string Vin { get; set; }
    public string OwnerType { get; set; }
    public string VehicleSite { get; set; }
    public string Transmission { get; set; }
    public bool DvsCompliant { get; set; }
    public string FuelPinSecretName { get; set; }
    public string FuelCardLastFour { get; set; }
    public string VehicleTestExpiry { get; set; }
    public string Notes { get; set; }
    public int? FleetioAssetId { get; set; }
    public string FleetioName { get; set; }
    public string FleetioStatus { get; set; }
}

public sealed class TrailerImportDto
{
    public Guid? Id { get; set; }
    public string TrailerNumber { get; set; }
    public string Type { get; set; }
    public string StandardCapacity { get; set; }
    public string EuroCapacity { get; set; }
    public bool Active { get; set; }
    public int? FleetioId { get; set; }
    public string FleetioName { get; set; }
    public string FleetioStatus { get; set; }
    public string Notes { get; set; }
}

public sealed class SiteImportDto
{
    public Guid? Id { get; set; }
    public string ExternalCode { get; set; }
    public string Name { get; set; }
    public string DriverTextName { get; set; }
    public string CollectionAddress { get; set; }
    public string CollectionInstructions { get; set; }
    public string MapLink { get; set; }
    public string Latitude { get; set; }
    public string Longitude { get; set; }
    public string OperationalRegion { get; set; }
    public bool Active { get; set; }
    public string CustomerCode { get; set; }
    public string GeofenceRadiusMetres { get; set; }
    public string CustomField1 { get; set; }
    public string CustomField2 { get; set; }
    public string CustomField3 { get; set; }
    public string DefaultTemperatureC { get; set; }
    public string Region { get; set; }
    public string PlanningProfileSource { get; set; }
    public string AddressCompleteness { get; set; }
}

public sealed class FuelCardImportDto
{
    public string Registration { get; set; }
    public string FuelProvider { get; set; }
    public string FuelPin { get; set; }
    public string ShellCard { get; set; }
    public string BpRedCard { get; set; }
    public string BpPlainCard { get; set; }
    public string FuelPinSecretName { get; set; }
    public string FuelCardLastFour { get; set; }
    public bool Active { get; set; }
}

#pragma warning restore CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
