using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/driver-master")]
[Authorize]
public sealed class DriverMasterManualController(TmsDbContext db) : ControllerBase
{
    [HttpPost("tachomaster/import-workers")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> ImportTachoWorkers([FromBody] IReadOnlyList<TachoWorkerImportRequest> records, CancellationToken ct)
    {
        if (records.Count == 0 || records.Count > 5000)
            return BadRequest(new { message = "Submit between 1 and 5000 TachoMaster worker rows." });

        var drivers = await db.Drivers.OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var actor = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "TMS user";

        var linked = 0;
        var updated = 0;
        var review = 0;
        var skipped = 0;
        var results = new List<object>();

        foreach (var record in records)
        {
            var memberCode = Clean(record.MemberCode, 80);
            var cardNumber = Clean(record.DriverCardNumber, 80);
            var employeeNumber = Clean(record.EmployeeNumber, 40);
            var workerName = Clean(record.WorkerName, 160);

            if (string.IsNullOrWhiteSpace(memberCode) && string.IsNullOrWhiteSpace(cardNumber) &&
                string.IsNullOrWhiteSpace(employeeNumber) && string.IsNullOrWhiteSpace(workerName))
            {
                skipped++;
                results.Add(new { status = "skipped", workerName, memberCode, reason = "No usable driver identity was supplied." });
                continue;
            }

            var candidates = new List<Driver>();
            string reason;
            var confidence = 0;

            if (!string.IsNullOrWhiteSpace(memberCode))
            {
                candidates = drivers.Where(driver => TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, memberCode)).ToList();
                reason = "TachoMaster Member Code";
                confidence = 100;
            }
            else
            {
                reason = string.Empty;
            }

            if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(cardNumber))
            {
                candidates = drivers.Where(driver => TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, cardNumber)).ToList();
                reason = "tachograph card";
                confidence = 99;
            }

            if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(employeeNumber))
            {
                candidates = drivers.Where(driver => string.Equals(driver.EmployeeNumber, employeeNumber, StringComparison.OrdinalIgnoreCase)).ToList();
                reason = "employee number";
                confidence = 98;
            }

            if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(workerName))
            {
                var normalWorker = TachoDriverIdentityRules.NormalisePerson(workerName);
                var nameMatches = drivers.Where(driver =>
                        TachoDriverIdentityRules.NormalisePerson(driver.DisplayName) == normalWorker ||
                        TachoDriverIdentityRules.NormalisePerson(driver.TachoName) == normalWorker)
                    .ToList();
                if (nameMatches.Count == 1)
                {
                    candidates = nameMatches;
                    reason = "unique normalised name";
                    confidence = 85;
                }
                else if (nameMatches.Count > 1)
                {
                    candidates = nameMatches;
                    reason = "ambiguous name";
                    confidence = 50;
                }
            }

            if (candidates.Count != 1)
            {
                review++;
                await StageWorkerReviewAsync(record, candidates, actor, ct);
                results.Add(new
                {
                    status = "review",
                    workerName,
                    memberCode,
                    cardNumber,
                    confidence,
                    reason = candidates.Count == 0
                        ? "No existing Driver Master record matched. The Tacho worker was retained for review and no new driver was created."
                        : $"More than one Driver Master record matched by {reason}.",
                    candidates = candidates.Select(driver => new { driver.Id, driver.EmployeeNumber, driver.DisplayName }).ToArray()
                });
                continue;
            }

            var driver = candidates[0];
            var changed = false;

            if (!string.IsNullOrWhiteSpace(memberCode) &&
                !string.Equals(driver.TachoMasterDriverId, memberCode, StringComparison.OrdinalIgnoreCase))
            {
                driver.TachoMasterDriverId = memberCode;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(cardNumber) &&
                !string.Equals(driver.TachoCardNumber, cardNumber, StringComparison.OrdinalIgnoreCase))
            {
                driver.TachoCardNumber = cardNumber;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(workerName) &&
                !string.Equals(driver.TachoName, workerName, StringComparison.OrdinalIgnoreCase))
            {
                driver.TachoName = workerName;
                changed = true;
            }

            var workerType = Clean(record.Type, 80);
            if (!string.IsNullOrWhiteSpace(workerType) &&
                !string.Equals(driver.DriverType, workerType, StringComparison.OrdinalIgnoreCase))
            {
                driver.DriverType = workerType;
                changed = true;
            }

            var agency = Clean(record.Agency, 160);
            if (!string.IsNullOrWhiteSpace(agency) &&
                !string.Equals(driver.AgencyName, agency, StringComparison.OrdinalIgnoreCase))
            {
                driver.AgencyName = agency;
                changed = true;
            }

            var email = Clean(record.Email, 320);
            if (!string.IsNullOrWhiteSpace(email) &&
                !string.Equals(driver.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                driver.Email = email;
                changed = true;
            }

            var cardExpiry = ParseDate(record.DriverCardExpiry);
            if (cardExpiry is not null && driver.DigitalTachoCardExpiry != cardExpiry) { driver.DigitalTachoCardExpiry = cardExpiry; changed = true; }
            var licenceExpiry = ParseDate(record.DrivingLicenceExpiry);
            if (licenceExpiry is not null && driver.LicenceExpiry != licenceExpiry) { driver.LicenceExpiry = licenceExpiry; changed = true; }
            var cpcExpiry = ParseDate(record.DqcExpiry) ?? ParseDate(record.CpcExpiry);
            if (cpcExpiry is not null && driver.CPCExpiry != cpcExpiry) { driver.CPCExpiry = cpcExpiry; changed = true; }

            if (changed)
            {
                updated++;
                await db.SaveChangesAsync(ct);
                await MasterDetailStore.SaveAsync(
                    db,
                    "driver",
                    driver.EmployeeNumber,
                    JsonSerializer.Serialize(new
                    {
                        driver.EmployeeNumber,
                        driver.DisplayName,
                        driver.TachoName,
                        driver.TachoMasterDriverId,
                        driver.TachoCardNumber,
                        driver.DriverType,
                        driver.AgencyName,
                        driver.Email,
                        driver.LicenceExpiry,
                        driver.CPCExpiry,
                        driver.DigitalTachoCardExpiry,
                        cardLastRead = record.CardLastRead,
                        started = record.Started,
                        source = "TachoMaster worker-list import"
                    }),
                    "TachoMaster worker-list import",
                    actor,
                    ct);
            }

            linked++;
            results.Add(new
            {
                status = changed ? "updated" : "linked",
                workerName,
                memberCode,
                cardNumber,
                confidence,
                reason,
                driverId = driver.Id,
                driver.EmployeeNumber,
                driver.DisplayName
            });
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { received = records.Count, linked, updated, review, skipped, results });
    }

    [HttpPut("{id:guid}/manual-details")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateManualDetails(Guid id, DriverMasterManualUpdateRequest request, CancellationToken ct)
    {
        var drivers = await db.Drivers.OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var driver = drivers.SingleOrDefault(item => item.Id == id);
        if (driver is null) return NotFound();

        var employeeNumber = CleanRequired(request.EmployeeNumber, 40);
        var displayName = CleanRequired(request.DisplayName, 160);
        if (string.IsNullOrWhiteSpace(employeeNumber) || string.IsNullOrWhiteSpace(displayName))
            return BadRequest(new { message = "Employee number and display name are required." });

        if (drivers.Any(item => item.Id != id && string.Equals(item.EmployeeNumber, employeeNumber, StringComparison.OrdinalIgnoreCase)))
            return Conflict(new { message = $"Employee number {employeeNumber} already exists." });

        var memberCode = Clean(request.TachoMasterDriverId, 80);
        if (!string.IsNullOrWhiteSpace(memberCode) && drivers.Any(item =>
                item.Id != id && item.Active &&
                TachoDriverIdentityRules.MemberMatches(item.TachoMasterDriverId, memberCode)))
            return Conflict(new { message = $"TachoMaster member/DB number {memberCode} is already linked to another active driver." });

        var cardNumber = Clean(request.TachoCardNumber, 80);
        if (!string.IsNullOrWhiteSpace(cardNumber) && drivers.Any(item =>
                item.Id != id && item.Active &&
                TachoDriverIdentityRules.CardsMatch(item.TachoCardNumber, cardNumber)))
            return Conflict(new { message = "That tachograph card number is already linked to another active driver." });

        driver.EmployeeNumber = employeeNumber;
        driver.DisplayName = displayName;
        driver.TachoName = Clean(request.TachoName, 160);
        driver.TachoMasterDriverId = memberCode;
        driver.TachoCardNumber = cardNumber;
        driver.MobileNumber = Clean(request.MobileNumber, 40);
        driver.DriverType = Clean(request.DriverType, 80);
        driver.DriverGroup = Clean(request.DriverGroup, 80);
        driver.Skills = Clean(request.Skills, 160);
        driver.Coding = Clean(request.Coding, 80);
        driver.AgencyName = Clean(request.AgencyName, 160);
        driver.NorthEligible = request.NorthEligible;
        driver.PreloadEligible = request.PreloadEligible;
        driver.Notes = Clean(request.Notes, 500);
        driver.DrivingLicenceNumber = Clean(request.DrivingLicenceNumber, 80);
        driver.LicenceExpiry = request.LicenceExpiry;
        driver.CPCExpiry = request.CPCExpiry;
        driver.DigitalTachoCardExpiry = request.DigitalTachoCardExpiry;
        driver.MedicalExpiry = request.MedicalExpiry;
        driver.LicenceStatus = Clean(request.LicenceStatus, 40);
        driver.Active = request.Active;

        await db.SaveChangesAsync(ct);
        await MasterDetailStore.SaveAsync(
            db,
            "driver",
            employeeNumber,
            JsonSerializer.Serialize(driver),
            "SLH driver editor",
            User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value,
            ct);

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Driver",
            EntityId = driver.Id,
            Action = "ManualDriverMasterDetailsUpdated",
            ChangedBy = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "unknown",
            ChangesJson = JsonSerializer.Serialize(new
            {
                driver.EmployeeNumber,
                driver.DisplayName,
                driver.TachoName,
                driver.TachoMasterDriverId,
                driver.TachoCardNumber,
                driver.DrivingLicenceNumber,
                driver.LicenceExpiry,
                driver.CPCExpiry,
                driver.DigitalTachoCardExpiry,
                driver.MedicalExpiry,
                driver.Active
            })
        });
        await db.SaveChangesAsync(ct);

        return Ok(driver);
    }

    private async Task StageWorkerReviewAsync(TachoWorkerImportRequest record, IReadOnlyCollection<Driver> candidates, string actor, CancellationToken ct)
    {
        var identity = Clean(record.MemberCode, 80)
            ?? Clean(record.DriverCardNumber, 80)
            ?? Clean(record.EmployeeNumber, 40)
            ?? TachoDriverIdentityRules.NormalisePerson(record.WorkerName);
        var key = $"driverreview:tacho-worker-import:{TachoDriverIdentityRules.NormaliseIdentifier(identity)}";
        var payload = JsonSerializer.Serialize(new
        {
            source = "TachoMaster worker-list import",
            record.MemberCode,
            record.WorkerName,
            record.Department,
            record.Type,
            record.EmployeeNumber,
            record.Agency,
            record.Email,
            record.Started,
            record.CardLastRead,
            record.DriverCardNumber,
            record.DriverCardExpiry,
            record.DrivingLicenceExpiry,
            record.CpcExpiry,
            record.DqcExpiry,
            candidates = candidates.Select(driver => new { driver.Id, driver.EmployeeNumber, driver.DisplayName }).ToArray()
        });

        var existing = await db.StagedImports.FirstOrDefaultAsync(item =>
            item.EntityType == "driverreview" && item.IdempotencyKey == key && item.Status == StagingStatus.PendingReview, ct);
        if (existing is null)
        {
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "driverreview",
                IdempotencyKey = key,
                PayloadJson = payload,
                Source = "TachoMaster worker-list import",
                Status = StagingStatus.PendingReview,
                ReviewNote = "Tacho worker did not match one canonical Driver Master record. No driver was created."
            });
        }
        else
        {
            existing.PayloadJson = payload;
            existing.ReviewedAtUtc = DateTimeOffset.UtcNow;
            existing.ReviewedBy = actor;
            existing.ReviewNote = "Tacho worker still requires Driver Master matching. No driver was created.";
        }
    }

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var formats = new[] { "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd" };
        return DateOnly.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateOnly.TryParse(value.Trim(), out parsed) ? parsed : null;
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string CleanRequired(string? value, int maxLength) => Clean(value, maxLength) ?? string.Empty;
}

public sealed record TachoWorkerImportRequest(
    string? MemberCode,
    string? WorkerName,
    string? Department,
    string? Type,
    string? EmployeeNumber,
    string? Agency,
    string? Email,
    string? Started,
    string? CardLastRead,
    string? DriverCardNumber,
    string? DriverCardExpiry,
    string? CardReadingState,
    string? Contract,
    string? LicencePassDate,
    string? DrivingLicenceExpiry,
    string? LicenceCheckDue,
    string? LicencePhotoExpiry,
    string? CpcExpiry,
    string? DoubleDeckerTrainingDate,
    string? AtWorkNow,
    string? DqcExpiry);

public sealed record DriverMasterManualUpdateRequest(
    string? EmployeeNumber,
    string? DisplayName,
    string? TachoName,
    string? TachoMasterDriverId,
    string? TachoCardNumber,
    string? MobileNumber,
    string? DriverType,
    string? DriverGroup,
    string? Skills,
    string? Coding,
    string? AgencyName,
    bool? NorthEligible,
    bool? PreloadEligible,
    string? Notes,
    string? DrivingLicenceNumber,
    DateOnly? LicenceExpiry,
    DateOnly? CPCExpiry,
    DateOnly? DigitalTachoCardExpiry,
    DateOnly? MedicalExpiry,
    string? LicenceStatus,
    bool Active);
