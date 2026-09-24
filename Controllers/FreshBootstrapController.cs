using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// One-time, admin-only bootstrap for an EMPTY clean V2 Driver Master.
/// TachoMaster Member Code is the identity and only drivers with a qualifying
/// card read in the previous six months are seeded.
/// </summary>
[ApiController, Route("api/v1/bootstrap")]
[Authorize(Policy = "TmsAdmin")]
public sealed class FreshBootstrapController(
    TmsDbContext db,
    IHttpClientFactory httpClientFactory,
    TachoMasterOptions tachoOptions,
    ILogger<FreshBootstrapController> logger) : ControllerBase
{
    [HttpPost("tachomaster-drivers")]
    public async Task<IActionResult> SeedTachoMasterDrivers([FromQuery] string confirm, CancellationToken ct)
    {
        if (!string.Equals(confirm, "EMPTY-DRIVER-MASTER", StringComparison.Ordinal))
            return BadRequest(new { message = "Confirmation must be EMPTY-DRIVER-MASTER." });

        if (!tachoOptions.IsConfigured)
            return BadRequest(new { message = "TachoMaster is not configured.", missingSettings = tachoOptions.MissingSettings });

        var existingCount = await db.Drivers.CountAsync(ct);
        if (existingCount != 0)
            return Conflict(new { message = "Fresh Tacho bootstrap is only permitted when Driver Master is empty.", existingCount });

        var raw = await new TachoLiveWorkerDirectory(httpClientFactory.CreateClient(), tachoOptions).GetLiveWorkersAsync(ct);
        var ukToday = TachoDriverCardReadEligibility.UkToday();
        var eligible = raw
            .Where(DriverPopulationRules.IsDriver)
            .Where(x => x.MemberCode > 0)
            .Where(x => TachoDriverCardReadEligibility.IsEligible(x.CardLastRead, ukToday))
            .GroupBy(x => x.MemberCode)
            .Select(g => g.OrderByDescending(x => !string.IsNullOrWhiteSpace(x.CardNumber))
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (eligible.Count < 25)
            return UnprocessableEntity(new
            {
                message = "TachoMaster returned too few recent-card drivers for a safe initial seed. No Driver rows were created.",
                rawCount = raw.Count,
                eligibleCount = eligible.Count
            });

        var employeeNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var actor = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "TMS.Admin";
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        foreach (var worker in eligible)
        {
            var employeeNumber = UniqueEmployeeNumber(worker, employeeNumbers);
            employeeNumbers.Add(employeeNumber);
            var driver = new Driver
            {
                EmployeeNumber = employeeNumber,
                DisplayName = worker.DisplayName.Trim(),
                TachoName = worker.DisplayName.Trim(),
                TachoMasterDriverId = worker.MemberCode.ToString(CultureInfo.InvariantCulture),
                MobileNumber = null,
                DriverType = Clean(worker.WorkerType),
                DriverGroup = null,
                DrivingLicenceNumber = null,
                DigitalTachoCardExpiry = ParseDate(worker.DriverCardExpiry),
                LicenceExpiry = ParseDate(worker.DrivingLicenceExpiry),
                CPCExpiry = ParseDate(worker.CpcExpiry) ?? ParseDate(worker.DqcExpiry),
                LastTachoSyncUtc = now,
                Active = true
            };
            db.Drivers.Add(driver);

            await MasterDetailStore.SaveAsync(
                db,
                "driver",
                employeeNumber,
                JsonSerializer.Serialize(new
                {
                    employeeNumber,
                    displayName = driver.DisplayName,
                    tachoName = driver.TachoName,
                    tachoMasterDriverId = driver.TachoMasterDriverId,
                    tachoCardNumber = worker.CardNumber,
                    workerType = worker.WorkerType,
                    agencyName = worker.AgencyName,
                    email = worker.Email,
                    cardLastRead = worker.CardLastRead,
                    driverCardExpiry = worker.DriverCardExpiry,
                    drivingLicenceExpiry = worker.DrivingLicenceExpiry,
                    cpcExpiry = worker.CpcExpiry,
                    dqcExpiry = worker.DqcExpiry,
                    source = "Clean V2 one-time TachoMaster bootstrap"
                }),
                "Clean V2 one-time TachoMaster bootstrap",
                actor,
                ct);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogWarning(
            "Clean V2 Driver Master bootstrapped from TachoMaster: {Created} unique recent-card Member Code driver(s) created by {Actor}.",
            eligible.Count, actor);

        return Ok(new
        {
            rawCount = raw.Count,
            created = eligible.Count,
            identity = "TachoMaster Member Code",
            eligibility = "Card read within previous six months",
            message = "Fresh Driver Master seeded once. This endpoint will now refuse to run because Driver Master is no longer empty."
        });
    }

    private static string UniqueEmployeeNumber(TachoLiveWorker worker, HashSet<string> used)
    {
        var preferred = Clean(worker.EmployeeNumber);
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            preferred = preferred.Length <= 40 ? preferred : preferred[..40];
            if (!used.Contains(preferred)) return preferred;
        }
        return $"TM-{worker.MemberCode}";
    }

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var formats = new[] { "d/M/yyyy", "dd/MM/yyyy", "d/M/yy", "dd/MM/yy", "yyyy-MM-dd" };
        return DateOnly.TryParseExact(value.Trim(), formats, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var date)
            ? date
            : DateOnly.TryParse(value.Trim(), CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AllowWhiteSpaces, out date) ? date : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
