using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Integrations;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/integrations/textbee"), AllowAnonymous]
public sealed class DriverSmsWebhookController(
    TmsDbContext db,
    TextBeeOptions textBee,
    ILogger<DriverSmsWebhookController> logger) : ControllerBase
{
    [HttpPost("webhook")]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(textBee.WebhookSigningSecret))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "TextBee webhook signing secret is not configured." });

        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        var signature = Request.Headers["X-Signature"].ToString();
        if (!VerifySignature(rawBody, signature, textBee.WebhookSigningSecret))
            return Unauthorized(new { message = "Invalid TextBee webhook signature." });

        TextBeeInboundMessage? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TextBeeInboundMessage>(rawBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "TextBee webhook payload was not valid JSON.");
            return BadRequest(new { message = "Invalid TextBee webhook payload." });
        }

        if (payload is null || !string.Equals(payload.WebhookEvent, "MESSAGE_RECEIVED", StringComparison.OrdinalIgnoreCase))
            return Ok(new { accepted = true, processed = false });

        if (string.IsNullOrWhiteSpace(payload.Message) || string.IsNullOrWhiteSpace(payload.Sender))
            return Ok(new { accepted = true, processed = false });

        if (!string.IsNullOrWhiteSpace(payload.SmsId) && await db.DriverStatusLogs.AnyAsync(
                item => item.Notes != null && item.Notes.Contains($"SMS ID {payload.SmsId}"), ct))
            return Ok(new { accepted = true, processed = false, duplicate = true });

        var sender = NormalisePhone(payload.Sender);
        var drivers = await db.Drivers
            .Where(item => item.Active && item.MobileNumber != null)
            .ToListAsync(ct);
        var matches = drivers.Where(item => NormalisePhone(item.MobileNumber) == sender).ToList();
        if (matches.Count != 1)
        {
            logger.LogWarning("TextBee inbound SMS from {Sender} could not be matched uniquely to an active driver. Matches={MatchCount}.", payload.Sender, matches.Count);
            return Ok(new { accepted = true, processed = false, matched = false });
        }

        var driver = matches[0];
        var receivedAtUtc = payload.ReceivedAt is DateTimeOffset received ? received : DateTimeOffset.UtcNow;
        var outgoing = await db.DriverStatusLogs
            .Where(item => item.DriverId == driver.Id &&
                           (item.Status == "Driver dispatched" || item.Status == "Driver text update sent") &&
                           item.CapturedAtUtc <= receivedAtUtc)
            .OrderByDescending(item => item.CapturedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (outgoing is null)
        {
            logger.LogInformation("TextBee inbound SMS from driver {DriverId} was received without a recent outbound dispatch/update.", driver.Id);
            return Ok(new { accepted = true, processed = false, matched = true, noOutbound = true });
        }

        var message = payload.Message.Trim().Replace("\r", string.Empty).Replace("\n", " · ");
        if (message.Length > 1000) message = message[..1000] + "…";
        db.DriverStatusLogs.Add(new Models.DriverStatusLog
        {
            LoadId = outgoing.LoadId,
            DriverId = driver.Id,
            Status = "Driver response received",
            Notes = $"Inbound SMS from {driver.DisplayName} via TextBee. SMS ID {payload.SmsId ?? "not supplied"}. {message}",
            CapturedBy = "TextBee inbound webhook"
        });
        await db.SaveChangesAsync(ct);

        return Ok(new { accepted = true, processed = true, matched = true, loadId = outgoing.LoadId });
    }

    private static bool VerifySignature(string rawBody, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        var receivedBytes = Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant());
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return receivedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(receivedBytes, expectedBytes);
    }

    private static string NormalisePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        if (digits.StartsWith("0", StringComparison.Ordinal) && digits.Length >= 10) digits = "44" + digits[1..];
        return digits.TrimStart('+');
    }

    private sealed record TextBeeInboundMessage(
        string? SmsId,
        string? Message,
        string? Sender,
        string? DeviceId,
        string? WebhookSubscriptionId,
        string? WebhookEvent,
        string? IdempotencyKey,
        DateTimeOffset? ReceivedAt);
}
