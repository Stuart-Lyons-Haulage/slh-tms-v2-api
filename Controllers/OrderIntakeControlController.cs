using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/order-intake/control-centre"), Authorize]
public sealed class OrderIntakeControlController(OrderIntakeReconciliationService reconciliation) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateTimeOffset? fromUtc, CancellationToken ct) =>
        Ok(await reconciliation.CompareAsync(fromUtc ?? DateTimeOffset.UtcNow.AddDays(-14), false, ct));

    [HttpGet("export.csv")]
    public async Task<IActionResult> Export([FromQuery] DateTimeOffset? fromUtc, CancellationToken ct)
    {
        var snapshot = await reconciliation.CompareAsync(fromUtc ?? DateTimeOffset.UtcNow.AddDays(-14), false, ct);
        var csv = new StringBuilder("Received,Sender,Subject,Direction,Status,Expected rows,Accounted rows,Pending review,Failed,Linked orders,Planned orders,Message ID,Detail\r\n");
        foreach (var row in snapshot.Records)
            csv.AppendLine(string.Join(',', new[] { row.ReceivedAtUtc.ToString("O"), row.Sender, row.Subject, row.Direction, row.Status,
                row.ExpectedRows.ToString(), row.AccountedRows.ToString(), row.PendingReview.ToString(), row.Failed.ToString(), row.LinkedOrders.ToString(),
                row.PlannedOrders.ToString(), row.MessageId, row.Detail }.Select(Csv)));
        return File(new UTF8Encoding(true).GetBytes(csv.ToString()), "text/csv", $"order-intake-reconciliation-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
