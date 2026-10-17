using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record MasterComplianceResult(bool Allowed, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public sealed class MasterAssignmentComplianceService(TmsDbContext db)
{
    private const int WarningWindowDays = 30;

    public async Task<MasterComplianceResult> CheckAsync(Guid? driverId, Guid? vehicleId, CancellationToken ct)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        if (driverId is Guid selectedDriverId)
        {
            var driver = await db.Drivers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == selectedDriverId, ct);
            if (driver is not null)
            {
                CheckDate(driver.LicenceExpiry, "driver licence", today, errors, warnings);
                CheckDate(driver.CPCExpiry, "driver CPC", today, errors, warnings);
                CheckDate(driver.DigitalTachoCardExpiry, "driver digital tacho card", today, errors, warnings);
                CheckDate(driver.MedicalExpiry, "driver medical", today, errors, warnings);
            }
        }

        if (vehicleId is Guid selectedVehicleId)
        {
            var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == selectedVehicleId, ct);
            if (vehicle is not null)
            {
                CheckDate(vehicle.MOTExpiry, "vehicle MOT", today, errors, warnings);
                CheckDate(vehicle.TachoCalibrationExpiry, "vehicle tacho calibration", today, errors, warnings);
            }
        }

        return new MasterComplianceResult(errors.Count == 0, errors, warnings);
    }

    private static void CheckDate(DateOnly? expiry, string label, DateOnly today, List<string> errors, List<string> warnings)
    {
        if (expiry is null) return;
        var date = expiry.Value;
        if (date < today)
        {
            errors.Add($"{label} expired on {date:dd/MM/yyyy}.");
            return;
        }
        if (date <= today.AddDays(WarningWindowDays))
            warnings.Add($"{label} expires on {date:dd/MM/yyyy}.");
    }
}
