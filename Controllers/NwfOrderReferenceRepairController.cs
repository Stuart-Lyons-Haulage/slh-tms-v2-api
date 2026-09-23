using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/staging/orders")]
[Authorize(Policy = "TmsWrite")]
public sealed class NwfOrderReferenceRepairController(TmsDbContext db) : ControllerBase
{
    [HttpPost("repair-nwf-references")]
    public async Task<IActionResult> Repair(CancellationToken ct)
    {
        var repaired = await NwfPendingOrderReferenceRepair.Apply(db, ct);
        return Ok(new
        {
            repaired,
            message = repaired == 0
                ? "No pending NWF references required correction."
                : $"Corrected {repaired} pending NWF pallet/crate/tray reference(s)."
        });
    }
}
