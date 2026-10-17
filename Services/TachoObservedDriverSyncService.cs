using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record TachoObservedDriverSyncResult(int Observed, int Existing, int Created, int SkippedUnknownVehicle, int SkippedWithoutCard, int StagedForReview = 0);

/// <summary>
/// Reconciles the live/open TachoMaster duty feed with Driver Master on every scheduled Tacho poll.
/// TachoMaster Member Code is the primary driver identity. The tachograph card is supporting evidence
/// and is only used as a fallback when the live status does not contain a valid Member Code.
/// A previously unseen driver is only allowed to be created when TachoMaster shows them in a vehicle
/// that exists in the SLH Vehicle Master.
/// </summary>
public sealed class TachoObservedDriverSyncService(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    ILogger<TachoObservedDriverSyncService> logger)
{
    public async Task<TachoObservedDriverSyncResult> SyncAsync(string actor, CancellationToken ct)
    {
        if (!tachoMaster.IsConfigured)
            return new(0, 0, 0, 0, 0);

        var today = UkOperatingDate(DateTimeOffset.UtcNow);
        var statusesByVehicle = await tachoMaster.GetOpenDriverStatusesByVehicleAsync(today, ct);
        var observed = statusesByVehicle.Values.SelectMany(value => value).ToList();
        if (observed.Count == 0)
            return new(0, 0, 0, 0, 0);

        var vehicles = await db.Vehicles.AsNoTracking().Where(vehicle => vehicle.Active).ToListAsync(ct);
        var knownVehicleKeys = vehicles
            .SelectMany(vehicle => new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormaliseIdentifier)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var drivers = await db.Drivers.OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);

        var existing = 0;
        var created = 0;
        var skippedUnknownVehicle = 0;
        var skippedWithoutCard = 0;
        var stagedForReview = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var status in observed
            .OrderBy(item => item.VehicleCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DriverName, StringComparer.OrdinalIgnoreCase))
        {
            var vehicleKey = NormaliseIdentifier(status.VehicleCode);
            var vehicleKnown = vehicleKey.Length > 0 && knownVehicleKeys.Contains(vehicleKey);

            var member = status.MemberCode > 0
                ? status.MemberCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
            var cardKey = NormaliseIdentifier(status.CardNumber);

            Driver? driver = null;
            if (!string.IsNullOrWhiteSpace(member))
            {
                driver = drivers.FirstOrDefault(candidate =>
                    string.Equals(candidate.TachoMasterDriverId?.Trim(), member, StringComparison.OrdinalIgnoreCase));
            }

            if (driver is null && cardKey.Length > 0)
            {
                driver = drivers.FirstOrDefault(candidate =>
                    NormaliseIdentifier(candidate.TachoCardNumber) == cardKey);
            }

            if (driver is not null)
            {
                if (!vehicleKnown)
                {
                    logger.LogWarning(
                        "TachoMaster observed existing driver {Driver} in vehicle {Vehicle}, but that vehicle code did not match the active Vehicle Master aliases. Updating the known driver identity and skipping new-driver creation for this vehicle alias.",
                        driver.DisplayName, status.VehicleCode);
                }

                if (!string.IsNullOrWhiteSpace(member)) driver.TachoMasterDriverId = member;
                if (cardKey.Length > 0) driver.TachoCardNumber = status.CardNumber;
                driver.TachoName = string.IsNullOrWhiteSpace(status.DriverName) ? driver.TachoName : status.DriverName.Trim();
                driver.TachoDriveAvailableTodayMinutes = status.DriveAvailableTodayMinutes ?? driver.TachoDriveAvailableTodayMinutes;
                driver.TachoDriveAvailableWeekMinutes = status.DriveAvailableWeekMinutes ?? driver.TachoDriveAvailableWeekMinutes;
                driver.TachoWorkAvailableWeekMinutes = status.WorkAvailableWeekMinutes ?? driver.TachoWorkAvailableWeekMinutes;
                driver.LastTachoSyncUtc = now;
                await MasterDetailStore.SaveAsync(db, "driver", driver.EmployeeNumber, JsonSerializer.Serialize(driver), "TachoMaster live vehicle identity", actor, ct);
                existing++;
                continue;
            }

            if (!vehicleKnown)
            {
                skippedUnknownVehicle++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(member) && cardKey.Length == 0)
            {
                skippedWithoutCard++;
                continue;
            }

            // Unknown Tacho identities must never create Driver Master rows during live polling.
            // Only Member Code identities are staged because Member Code is the canonical person identity.
            if (string.IsNullOrWhiteSpace(member))
            {
                skippedWithoutCard++;
                continue;
            }

            var reviewKey = $"driverreview:member:{member}";
            var payloadJson = JsonSerializer.Serialize(new
            {
                tachoMemberCode = member,
                displayName = string.IsNullOrWhiteSpace(status.DriverName) ? $"Tacho driver {member}" : status.DriverName.Trim(),
                cardNumber = string.IsNullOrWhiteSpace(status.CardNumber) ? null : status.CardNumber.Trim(),
                employeeNumber = (string?)null,
                workerType = "Driver",
                agencyName = (string?)null,
                cardLastRead = (string?)null,
                driverCardExpiry = (string?)null,
                drivingLicenceExpiry = (string?)null,
                cpcExpiry = (string?)null,
                source = "TachoMaster live duty observation — no matching Driver Master record",
                vehicleCode = status.VehicleCode,
                receivedAtUtc = now
            });

            var review = await db.StagedImports
                .SingleOrDefaultAsync(row => row.EntityType == "driverreview" && row.IdempotencyKey == reviewKey, ct);
            if (review is null)
            {
                db.StagedImports.Add(new StagedImport
                {
                    EntityType = "driverreview",
                    IdempotencyKey = reviewKey,
                    PayloadJson = payloadJson,
                    Source = "TachoMaster live duty identity",
                    Status = StagingStatus.PendingReview,
                    ReceivedAtUtc = now,
                    ReviewNote = $"TachoMaster member {member} ({status.DriverName}) was observed in SLH vehicle {status.VehicleCode} but has no Driver Master match. Review before creating a driver."
                });
                stagedForReview++;
            }
            else if (review.Status == StagingStatus.PendingReview)
            {
                review.PayloadJson = payloadJson;
                review.ReviewNote = $"Updated from latest TachoMaster live duty observation at {now:u}; still awaiting Driver Master review.";
            }

            logger.LogWarning(
                "TachoMaster member {MemberCode} observed in SLH vehicle {Vehicle} has no canonical Driver Master match; staged for review instead of creating a driver.",
                member, status.VehicleCode);
        }

        if (existing > 0 || stagedForReview > 0 || db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);

        return new(observed.Count, existing, 0, skippedUnknownVehicle, skippedWithoutCard, stagedForReview);
    }

    private static string UniqueReference(string? member, string cardKey, IReadOnlyCollection<Driver> drivers)
    {
        var used = drivers.Select(driver => driver.EmployeeNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = !string.IsNullOrWhiteSpace(member)
            ? Clip($"TM-{member}", 40)
            : Clip($"TACHO-{(cardKey.Length <= 12 ? cardKey : cardKey[^12..])}", 40);
        if (!used.Contains(root)) return root;

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Clip($"{root}-{index}", 40);
            if (!used.Contains(candidate)) return candidate;
        }

        return Clip($"TM-{Guid.NewGuid():N}", 40);
    }

    private static string NormaliseIdentifier(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Clip(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}