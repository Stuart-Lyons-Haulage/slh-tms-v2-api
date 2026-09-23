using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/master")]
[Authorize(Policy = "TmsReadMaster")]
public sealed class MasterController(MasterDataService master) : ControllerBase
{
    [HttpGet("drivers")]
    public Task<IReadOnlyList<MasterDriver>> Drivers(CancellationToken ct) => master.GetActiveDriversAsync(ct);

    [HttpGet("vehicles")]
    public Task<IReadOnlyList<MasterVehicle>> Vehicles(CancellationToken ct) => master.GetActiveVehiclesAsync(ct);

    [HttpGet("trailers")]
    public Task<IReadOnlyList<MasterTrailer>> Trailers(CancellationToken ct) => master.GetActiveTrailersAsync(ct);

    [HttpGet("depots")]
    public Task<IReadOnlyList<MasterDepot>> Depots(CancellationToken ct) => master.GetActiveDepotsAsync(ct);

    [HttpGet("customers")]
    public Task<IReadOnlyList<MasterCustomer>> Customers(CancellationToken ct) => master.GetActiveCustomersAsync(ct);

    [HttpGet("sites")]
    public Task<IReadOnlyList<MasterSite>> Sites(CancellationToken ct) => master.GetActiveSitesAsync(ct);

    [HttpGet("sites/{siteId}")]
    public async Task<ActionResult<MasterSite>> Site(string siteId, CancellationToken ct) =>
        await master.GetSiteByIdAsync(siteId, ct) is { } site ? site : NotFound();

    [HttpGet("subcontractors")]
    public Task<IReadOnlyList<MasterSubcontractor>> Subcontractors(CancellationToken ct) => master.GetActiveSubcontractorsAsync(ct);

    [HttpGet("markets")]
    public Task<IReadOnlyList<MasterMarket>> Markets(CancellationToken ct) => master.GetActiveMarketsAsync(ct);

    [HttpGet("fuel-cards")]
    public Task<IReadOnlyList<MasterFuelCard>> FuelCards(CancellationToken ct) => master.GetActiveFuelCardsAsync(ct);

    [HttpGet("fuel-prices")]
    public Task<IReadOnlyList<MasterFuelPrice>> FuelPrices(CancellationToken ct) => master.GetActiveFuelPricesAsync(ct);

    [HttpPost("cache/invalidate")]
    [Authorize(Policy = "TmsAdmin")]
    public IActionResult InvalidateCache()
    {
        master.Invalidate();
        return NoContent();
    }
}
