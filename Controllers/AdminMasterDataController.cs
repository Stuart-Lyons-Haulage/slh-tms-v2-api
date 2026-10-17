using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Admin endpoint for bulk master data import from CSV files.
/// Restricted to authenticated admin users (Entra role: "TmsAdmin" or Claim: "admin").
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize]
public sealed class AdminMasterDataController(CsvMasterDataImportService importService, MasterDataService masterDataService) : ControllerBase
{
    /// <summary>
    /// POST: Import vehicles from CSV
    /// Body: multipart form with "file" field (Vehicles.csv)
    /// Returns: { tableNamme, requestedRowCount, insertedRows, skippedRows, errors[] }
    /// </summary>
    [HttpPost("import/vehicles")]
    [ProducesResponseType(typeof(ImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ImportVehicles(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "File is required" });

        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "File must be CSV" });

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await importService.ImportVehiclesAsync(stream, ct);
            masterDataService.Invalidate();
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST: Import trailers from CSV
    /// Body: multipart form with "file" field (Trailers.csv)
    /// Returns: { tableName, requestedRowCount, insertedRows, skippedRows, errors[] }
    /// </summary>
    [HttpPost("import/trailers")]
    [ProducesResponseType(typeof(ImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ImportTrailers(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "File is required" });

        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "File must be CSV" });

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await importService.ImportTrailersAsync(stream, ct);
            masterDataService.Invalidate();
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST: Import sites from CSV
    /// Body: multipart form with "file" field (Sites.csv)
    /// Returns: { tableName, requestedRowCount, insertedRows, skippedRows, errors[] }
    /// </summary>
    [HttpPost("import/sites")]
    [ProducesResponseType(typeof(ImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ImportSites(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "File is required" });

        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "File must be CSV" });

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await importService.ImportSitesAsync(stream, ct);
            masterDataService.Invalidate();
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST: Import fuel cards from CSV
    /// Body: multipart form with "file" field (Fuel_Cards.csv)
    /// Returns: { tableName, requestedRowCount, insertedRows, skippedRows, errors[] }
    /// </summary>
    [HttpPost("import/fuel-cards")]
    [ProducesResponseType(typeof(ImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ImportFuelCards(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "File is required" });

        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "File must be CSV" });

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await importService.ImportFuelCardsAsync(stream, ct);
            masterDataService.Invalidate();
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
