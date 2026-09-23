using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/integrations/sage-hr/driver-leave")]
[Authorize]
public sealed class SageHrLeaveController(
    SageHrClient sageHr,
    TmsDbContext db,
    ILogger<SageHrLeaveController> logger) : ControllerBase
{
    private const int MaxDays = 14;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? from, [FromQuery] int days = 5, CancellationToken ct = default)
    {
        if (!sageHr.IsConfigured)
        {
            return Ok(new SageHrDriverLeaveResponse(
                false,
                "Unavailable",
                DateOnly.FromDateTime(DateTime.UtcNow),
                0,
                DateTimeOffset.UtcNow,
                [],
                sageHr.MissingSettings,
                $"Sage HR leave cannot be checked until these settings are complete: {string.Join(", ", sageHr.MissingSettings)}."));
        }

        var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var requestedDays = Math.Clamp(days, 1, MaxDays);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var employees = await sageHr.GetActiveEmployeesAsync(timeout.Token);
            var drivers = employees
                .Where(employee => DriverPopulationRules.IsSageDriver(employee, sageHr.DriverTeamName, sageHr.DriverPositionKeyword))
                .Where(employee => !string.IsNullOrWhiteSpace(employee.EmployeeNumber))
                .GroupBy(employee => Normalise(employee.EmployeeNumber), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToDictionary(employee => Normalise(employee.EmployeeNumber), StringComparer.OrdinalIgnoreCase);

            var tmsDrivers = await db.Drivers.AsNoTracking()
                .Where(driver => driver.Active && driver.EmployeeNumber != null && driver.EmployeeNumber != string.Empty)
                .Select(driver => new { driver.Id, driver.EmployeeNumber, driver.DisplayName, driver.DriverType, driver.DriverGroup })
                .ToListAsync(timeout.Token);
            var tmsByEmployee = tmsDrivers
                .GroupBy(driver => Normalise(driver.EmployeeNumber), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var rows = new List<SageHrDriverLeaveItem>();
            for (var offset = 0; offset < requestedDays; offset++)
            {
                var date = start.AddDays(offset);
                var leaveItems = await sageHr.GetOutOfOfficeAsync(date, timeout.Token);
                foreach (var leave in leaveItems)
                {
                    var employee = employees.FirstOrDefault(item => item.Id == leave.EmployeeId);
                    if (employee is null || string.IsNullOrWhiteSpace(employee.EmployeeNumber)) continue;
                    var employeeKey = Normalise(employee.EmployeeNumber);
                    if (!drivers.ContainsKey(employeeKey)) continue;

                    tmsByEmployee.TryGetValue(employeeKey, out var tmsDriver);
                    var displayName = tmsDriver?.DisplayName ?? $"{employee.FirstName} {employee.LastName}".Trim();
                    if (string.IsNullOrWhiteSpace(displayName)) displayName = employee.EmployeeNumber.Trim();

                    rows.Add(new SageHrDriverLeaveItem(
                        date,
                        tmsDriver?.Id,
                        employee.EmployeeNumber.Trim(),
                        displayName,
                        employee.Id,
                        leave.Policy?.Name,
                        leave.Details,
                        leave.IsPartOfDay,
                        leave.Hours,
                        leave.StartDate,
                        leave.EndDate,
                        tmsDriver is not null));
                }
            }

            var ordered = rows
                .GroupBy(row => $"{row.Date:yyyy-MM-dd}|{Normalise(row.EmployeeNumber)}|{row.PolicyName}|{row.StartDate}|{row.EndDate}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(row => row.Date)
                .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var source = ordered.Count == 0
                ? "Sage HR leave checked; no employed drivers are off in the selected window."
                : $"Sage HR leave checked; {ordered.Count} driver leave record(s) found in the selected window.";

            return Ok(new SageHrDriverLeaveResponse(
                true,
                "Sage HR",
                start,
                requestedDays,
                DateTimeOffset.UtcNow,
                ordered,
                [],
                source));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Sage HR driver leave lookup exceeded the 20 second dashboard budget.");
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { configured = true, source = "Sage HR", message = "Sage HR leave lookup timed out before the dashboard could be updated." });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Sage HR driver leave lookup failed.");
            return StatusCode(StatusCodes.Status502BadGateway, new { configured = true, source = "Sage HR", message = $"Sage HR leave lookup failed: {exception.GetBaseException().Message}" });
        }
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

public sealed record SageHrDriverLeaveResponse(
    bool Configured,
    string Source,
    DateOnly From,
    int Days,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<SageHrDriverLeaveItem> Items,
    IReadOnlyList<string> MissingSettings,
    string Message);

public sealed record SageHrDriverLeaveItem(
    DateOnly Date,
    Guid? DriverId,
    string EmployeeNumber,
    string DisplayName,
    long SageHrEmployeeId,
    string? PolicyName,
    string? Details,
    bool IsPartDay,
    double? Hours,
    string? StartDate,
    string? EndDate,
    bool LinkedToDriverMaster);
