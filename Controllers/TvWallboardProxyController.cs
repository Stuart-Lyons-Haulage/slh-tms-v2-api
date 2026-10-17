using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/tv-display/wallboard-proxy")]
public sealed class TvWallboardProxyController(
    TmsDbContext db,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<TvWallboardProxyController> logger) : ControllerBase
{
    [HttpGet("delivery-etas"), AllowAnonymous]
    public Task<IActionResult> DeliveryEtas(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? date,
        CancellationToken ct) => ProxyAsync("api/v1/operations/delivery-etas", displayKey, date, ct);

    [HttpGet("run-progress"), AllowAnonymous]
    public Task<IActionResult> RunProgress(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? date,
        CancellationToken ct) => ProxyAsync("api/v1/run-progress", displayKey, date, ct);

    private async Task<IActionResult> ProxyAsync(string targetPath, string? displayKey, DateOnly? date, CancellationToken ct)
    {
        var suppliedKey = displayKey;
        if (string.IsNullOrWhiteSpace(suppliedKey) && Request.Query.TryGetValue("key", out var queryKey))
            suppliedKey = queryKey.FirstOrDefault();

        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, suppliedKey, ct);
        var legacyKeyAllowed = TvWallboardAccess.IsAllowed(HttpContext, configuration);
        if (!pairedKeyAllowed && !legacyKeyAllowed)
            return Unauthorized(new { message = "This TV display is not paired." });

        var serverKey = TvWallboardAccess.ConfiguredKey(configuration);
        if (string.IsNullOrWhiteSpace(serverKey))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "The server wallboard key is not configured." });

        var query = date is null ? string.Empty : $"?date={Uri.EscapeDataString(date.Value.ToString("yyyy-MM-dd"))}";
        var target = $"{Request.Scheme}://{Request.Host}/{targetPath}{query}";

        using var outbound = new HttpRequestMessage(HttpMethod.Get, target);
        outbound.Headers.TryAddWithoutValidation(TvWallboardAccess.HeaderName, serverKey);

        try
        {
            using var response = await httpClientFactory.CreateClient("tv-wallboard-proxy").SendAsync(outbound, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                Content = content,
                ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json"
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = "The live wallboard feed timed out; retain the previous confirmed snapshot." });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TV wallboard proxy failed for {TargetPath}.", targetPath);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "The live wallboard feed is temporarily unavailable; retain the previous confirmed snapshot." });
        }
    }
}
