using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/fuel-prices")]
[Authorize]
public sealed class FuelController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int take = 1000, CancellationToken ct = default)
    {
        var rows = await db.FuelPrices.AsNoTracking()
            .OrderByDescending(item => item.WeekCommencing)
            .ThenBy(item => item.Provider)
            .Take(Math.Clamp(take, 1, 5000))
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Upsert(FuelPriceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Provider)) return BadRequest("Provider is required.");
        if (request.PricePencePerLitre <= 0) return BadRequest("Price must be greater than zero.");
        var provider = request.Provider.Trim();
        var row = await db.FuelPrices.SingleOrDefaultAsync(item => item.WeekCommencing == request.WeekCommencing && item.Provider == provider, ct);
        if (row is null)
        {
            row = new FuelPrice { WeekCommencing = request.WeekCommencing, Provider = provider, PricePencePerLitre = request.PricePencePerLitre, IsPricingMaximum = request.IsPricingMaximum, Source = request.Source, Notes = request.Notes };
            db.FuelPrices.Add(row);
        }
        else
        {
            row.PricePencePerLitre = request.PricePencePerLitre;
            row.IsPricingMaximum = request.IsPricingMaximum;
            row.Source = request.Source;
            row.Notes = request.Notes;
        }
        await db.SaveChangesAsync(ct);
        return Ok(row);
    }
}

public sealed record FuelPriceRequest(DateOnly WeekCommencing, string Provider, decimal PricePencePerLitre, bool IsPricingMaximum, string? Source, string? Notes);
