using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/integrations/tachomaster/identity")]
[Authorize(Policy = "TmsWrite")]
public sealed class TachoMasterIdentityController(TmsDbContext db, ILogger<TachoMasterIdentityController> logger) : ControllerBase
{
    [HttpPost("workers")]
    public async Task<IActionResult> ImportWorkers([FromBody] IReadOnlyList<TachoWorkerImportRow> rows, CancellationToken ct)
    {
        try
        {
            await EnsureIdentitySchemaAsync(ct);
            var drivers = await db.Drivers.Where(x => x.Active).OrderBy(x => x.DisplayName).ToListAsync(ct);
            await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
            var matched = 0;
            var updated = 0;
            var ambiguous = 0;
            var unmatched = 0;

            foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r.WorkerName)))
            {
                var candidates = MatchDrivers(row, drivers);
                if (candidates.Count == 0) { unmatched++; continue; }
                if (candidates.Count != 1) { ambiguous++; continue; }

                var driver = candidates[0];
                matched++;
                var cleanName = DisplayTachoName(row.WorkerName);
                if (!string.IsNullOrWhiteSpace(cleanName) && !string.Equals(driver.TachoName, cleanName, StringComparison.Ordinal))
                {
                    driver.TachoName = cleanName;
                    updated++;
                }

                driver.TachoMasterDriverId = Clean(row.MemberCode);
                driver.TachoCardNumber = Clean(row.DriverCardNumber);
                driver.LastTachoSyncUtc = DateTimeOffset.UtcNow;

                await UpsertMapping("TachoMaster", Clean(row.MemberCode), cleanName, "Driver", driver.Id, "Member code", ct);
                await UpsertMapping("TachoMasterCard", Clean(row.DriverCardNumber), cleanName, "Driver", driver.Id, "Driver card number", ct);
                await UpsertMapping("TachoMasterName", NormalisePersonName(cleanName), cleanName, "Driver", driver.Id, "Normalised worker name", ct);
                if (!string.IsNullOrWhiteSpace(row.EmployeeNumber))
                    await UpsertMapping("TachoMasterEmployee", Clean(row.EmployeeNumber), cleanName, "Driver", driver.Id, "Employee number", ct);

                await MasterDetailStore.SaveAsync(db, "driver", driver.EmployeeNumber, JsonSerializer.Serialize(driver), "TachoMaster Worker List", User.Identity?.Name, ct);
            }

            await db.SaveChangesAsync(ct);
            return Ok(new
            {
                source = rows.Count,
                matched,
                updated,
                ambiguous,
                unmatched,
                message = $"TachoMaster Worker List: {matched} matched, {updated} Tacho names updated, {ambiguous} ambiguous and {unmatched} unmatched. No drivers were created."
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "TachoMaster Worker List import failed.");
            return Problem(title: "TachoMaster Worker List import failed", detail: exception.GetBaseException().Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    [HttpPost("vehicles")]
    public async Task<IActionResult> ImportVehicles([FromBody] IReadOnlyList<TachoVehicleImportRow> rows, CancellationToken ct)
    {
        try
        {
            await EnsureIdentitySchemaAsync(ct);
            var vehicles = await db.Vehicles.Where(x => x.Active).OrderBy(x => x.Registration).ToListAsync(ct);
            var matched = 0;
            var ambiguous = 0;
            var unmatched = 0;

            foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r.Vehicle)))
            {
                var key = NormaliseVehicle(row.Vehicle);
                var candidates = vehicles.Where(v => NormaliseVehicle(v.Registration) == key || NormaliseVehicle(v.FleetNumber) == key || NormaliseVehicle(v.Abbreviation) == key).ToList();
                if (candidates.Count == 0) { unmatched++; continue; }
                if (candidates.Count != 1) { ambiguous++; continue; }

                var vehicle = candidates[0];
                matched++;
                await UpsertMapping("TachoMaster", key, row.Vehicle.Trim(), "Vehicle", vehicle.Id, "TachoMaster registration", ct);
                await UpsertMapping("DotTracking", key, row.Vehicle.Trim(), "Vehicle", vehicle.Id, "RoadTech/Tacho registration alignment", ct);
                if (!string.IsNullOrWhiteSpace(row.Vin))
                    await UpsertMapping("TachoMasterVIN", NormaliseVehicle(row.Vin), row.Vin.Trim(), "Vehicle", vehicle.Id, "VIN", ct);
            }

            await db.SaveChangesAsync(ct);
            return Ok(new
            {
                source = rows.Count,
                matched,
                ambiguous,
                unmatched,
                message = $"TachoMaster Vehicle List: {matched} existing vehicles linked, {ambiguous} ambiguous and {unmatched} unmatched. No vehicles were created."
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "TachoMaster Vehicle List import failed.");
            return Problem(title: "TachoMaster Vehicle List import failed", detail: exception.GetBaseException().Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private async Task EnsureIdentitySchemaAsync(CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'dbo.IntegrationMappings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IntegrationMappings (
        Id uniqueidentifier NOT NULL,
        Provider nvarchar(40) NOT NULL,
        ExternalKey nvarchar(200) NOT NULL,
        ExternalLabel nvarchar(200) NULL,
        TmsEntityType nvarchar(20) NOT NULL,
        TmsEntityId uniqueidentifier NOT NULL,
        Active bit NOT NULL CONSTRAINT DF_IntegrationMappings_Active DEFAULT (1),
        Notes nvarchar(1000) NULL,
        CreatedAtUtc datetimeoffset NOT NULL,
        UpdatedAtUtc datetimeoffset NOT NULL,
        UpdatedBy nvarchar(200) NULL,
        CONSTRAINT PK_IntegrationMappings PRIMARY KEY (Id)
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IntegrationMappings_Provider_ExternalKey_Type' AND object_id = OBJECT_ID(N'dbo.IntegrationMappings'))
    CREATE UNIQUE INDEX IX_IntegrationMappings_Provider_ExternalKey_Type ON dbo.IntegrationMappings(Provider, ExternalKey, TmsEntityType) WHERE Active = 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IntegrationMappings_TmsEntityId' AND object_id = OBJECT_ID(N'dbo.IntegrationMappings'))
    CREATE INDEX IX_IntegrationMappings_TmsEntityId ON dbo.IntegrationMappings(TmsEntityId);", ct);
    }

    private static List<Driver> MatchDrivers(TachoWorkerImportRow row, IReadOnlyList<Driver> drivers)
    {
        var member = Clean(row.MemberCode);
        var card = Clean(row.DriverCardNumber);
        var employee = Clean(row.EmployeeNumber);
        var sourceName = NormalisePersonName(DisplayTachoName(row.WorkerName));

        if (!string.IsNullOrWhiteSpace(card))
        {
            var byCard = drivers.Where(d => string.Equals(Clean(d.TachoCardNumber), card, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byCard.Count > 0) return byCard;
        }
        if (!string.IsNullOrWhiteSpace(member))
        {
            var byMember = drivers.Where(d => string.Equals(Clean(d.TachoMasterDriverId), member, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byMember.Count > 0) return byMember;
        }
        if (!string.IsNullOrWhiteSpace(employee))
        {
            var byEmployee = drivers.Where(d => string.Equals(Clean(d.EmployeeNumber), employee, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byEmployee.Count > 0) return byEmployee;
        }
        return drivers.Where(d => new[] { d.TachoName, d.DisplayName }.Any(n => NormalisePersonName(n) == sourceName)).ToList();
    }

    private async Task UpsertMapping(string provider, string? externalKey, string? label, string entityType, Guid entityId, string notes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalKey)) return;
        var existing = await db.IntegrationMappings.FirstOrDefaultAsync(x => x.Provider == provider && x.ExternalKey == externalKey && x.TmsEntityType == entityType && x.Active, ct);
        if (existing is null)
        {
            db.IntegrationMappings.Add(new IntegrationMapping
            {
                Provider = provider,
                ExternalKey = externalKey,
                ExternalLabel = label,
                TmsEntityType = entityType,
                TmsEntityId = entityId,
                Active = true,
                Notes = notes,
                UpdatedBy = User.Identity?.Name
            });
        }
        else
        {
            existing.TmsEntityId = entityId;
            existing.ExternalLabel = label;
            existing.Notes = notes;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            existing.UpdatedBy = User.Identity?.Name;
        }
    }

    private static string Clean(string? value) => (value ?? string.Empty).Trim();
    private static string NormaliseVehicle(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string NormalisePersonName(string? value) => string.Join(' ', (value ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
        .Where(word => word.Length > 0).OrderBy(word => word, StringComparer.Ordinal));
    private static string DisplayTachoName(string? workerName)
    {
        var value = Clean(workerName);
        var comma = value.IndexOf(',');
        return comma > 0 ? $"{value[(comma + 1)..].Trim()} {value[..comma].Trim()}".Trim() : value;
    }
}

public sealed record TachoWorkerImportRow(string? MemberCode, string? WorkerName, string? EmployeeNumber, string? DriverCardNumber);
public sealed record TachoVehicleImportRow(string? Vehicle, string? Site, string? OwnerType, string? Vin);
