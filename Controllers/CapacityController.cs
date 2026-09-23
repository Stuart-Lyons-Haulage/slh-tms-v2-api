using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/capacity")]
[Authorize]
public sealed class CapacityController : ControllerBase
{
    [HttpPost("check")]
    public IActionResult Check(PalletCapacityRequest request)
    {
        try
        {
            return Ok(PalletCapacityCalculator.Calculate(
                request.StandardPallets,
                request.EuroPallets,
                request.UnknownPallets,
                request.StandardCapacity ?? PalletCapacityCalculator.DefaultStandardCapacity,
                request.EuroCapacity ?? PalletCapacityCalculator.DefaultEuroCapacity));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return BadRequest(new { error = "invalid_capacity_input", message = exception.Message });
        }
    }
}

public sealed record PalletCapacityRequest(
    decimal? StandardPallets,
    decimal? EuroPallets,
    decimal? UnknownPallets = null,
    decimal? StandardCapacity = null,
    decimal? EuroCapacity = null);
