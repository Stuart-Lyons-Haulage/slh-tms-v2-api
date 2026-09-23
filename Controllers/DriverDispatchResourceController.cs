using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/driver-dispatch/resources")]
[Authorize(Policy = "TmsWrite")]
public sealed class DriverDispatchResourceController(TmsDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Add(DispatchResourceRequest request, CancellationToken ct)
    {
        var name = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "Enter the driver or subcontractor name." });

        var type = CanonicalType(request.DriverType);
        if (type is null)
            return BadRequest(new { message = "Type must be Employed, Casual, Agency or Subcontractor." });

        var organisation = request.OrganisationName?.Trim();
        if ((type is "Agency" or "Subcontractor") && string.IsNullOrWhiteSpace(organisation))
            organisation = name;

        var employeeNumber = request.EmployeeNumber?.Trim();
        if (type is "Employed" or "Casual")
        {
            if (string.IsNullOrWhiteSpace(employeeNumber))
                return BadRequest(new { message = "Enter the employee number, or sync Driver Master first." });
        }
        else if (string.IsNullOrWhiteSpace(employeeNumber))
        {
            var prefix = type == "Subcontractor" ? "SUB" : "AGY";
            employeeNumber = $"{prefix}-{DateTime.UtcNow:yyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        }

        var byEmployee = await db.Drivers.FirstOrDefaultAsync(x => x.Active && x.EmployeeNumber == employeeNumber, ct);
        if (byEmployee is not null)
            return Ok(new { id = byEmployee.Id, message = $"{byEmployee.DisplayName} is already in Driver Dispatch." });

        var nameMatches = await db.Drivers
            .Where(x => x.Active && x.DisplayName.ToUpper() == name.ToUpper())
            .ToListAsync(ct);
        if (nameMatches.Count > 1)
            return Conflict(new { message = "More than one active Driver Master record has that name. Review Driver Master before adding another." });
        if (nameMatches.Count == 1)
            return Ok(new { id = nameMatches[0].Id, message = $"{nameMatches[0].DisplayName} is already in Driver Dispatch." });

        var driver = new Driver
        {
            EmployeeNumber = employeeNumber!,
            DisplayName = name,
            DriverType = type,
            DriverGroup = type is "Agency" or "Subcontractor" ? organisation : type,
            Active = true
        };

        db.Drivers.Add(driver);
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            id = driver.Id,
            driverType = type,
            message = type == "Subcontractor"
                ? $"Subbie {name} added and is now available in Driver Dispatch."
                : $"{name} added and is now available in Driver Dispatch."
        });
    }

    private static string? CanonicalType(string? value)
    {
        if (string.Equals(value?.Trim(), "Employed", StringComparison.OrdinalIgnoreCase)) return "Employed";
        if (string.Equals(value?.Trim(), "Casual", StringComparison.OrdinalIgnoreCase)) return "Casual";
        if (string.Equals(value?.Trim(), "Agency", StringComparison.OrdinalIgnoreCase)) return "Agency";
        if (string.Equals(value?.Trim(), "Subcontractor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value?.Trim(), "Subbie", StringComparison.OrdinalIgnoreCase)) return "Subcontractor";
        return null;
    }
}

public sealed record DispatchResourceRequest(
    string? DisplayName,
    string? EmployeeNumber,
    string? DriverType,
    string? OrganisationName);
