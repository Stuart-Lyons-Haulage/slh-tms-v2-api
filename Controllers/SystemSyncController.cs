using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/system-sync")]
[Authorize]
public sealed class SystemSyncController(
    IntegrationSyncCoordinator coordinator,
    DependencyHealthService health) : ControllerBase
{
    [HttpGet("state")]
    public async Task<IActionResult> State(CancellationToken ct)
    {
        var snapshot = await health.GetSnapshotAsync(ct);
        var providers = new[]
        {
            RoadTechProvider(snapshot),
            Provider("Fleetio", "Fleetio", snapshot),
            Provider("Sage HR", "Sage HR", snapshot)
        };
        var configured = providers.Where(item => item.Configured).ToArray();
        var status = configured.Any(item => item.State == "stale")
            ? "attention"
            : configured.Any(item => item.State == "delayed")
                ? "pending"
                : "current";
        var lastPlatformUpdateUtc = providers.Select(item => item.LastUpdatedUtc).Where(value => value is not null).Max();

        return Ok(new
        {
            status,
            generatedAtUtc = snapshot.CheckedAtUtc,
            lastPlatformUpdateUtc,
            displaySource = "Canonical TMS integration state",
            schedules = new
            {
                roadTech = "tracking live every minute · history every 5 minutes · tacho every 20 minutes",
                fleetio = "every hour",
                sageHr = "05:30 Europe/London daily"
            },
            providers
        });
    }

    [HttpPost("force/{provider}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Force(string provider, CancellationToken ct)
    {
        var actor = User.Identity?.Name ?? "admin:manual";
        return provider.Trim().ToLowerInvariant() switch
        {
            "roadtech" or "road-tech" or "tacho" or "tachomaster" => Ok(await coordinator.SyncTachoMasterAsync(actor, ct)),
            "sage" or "sagehr" or "sage-hr" => Ok(await coordinator.SyncSageHrAsync(actor, ct)),
            "fleetio" => Ok(await coordinator.SyncFleetioAsync(actor, ct)),
            "all" => Ok(await coordinator.ForceAllAsync(actor, ct)),
            _ => BadRequest(new { message = "Provider must be roadtech, sage, fleetio or all. RoadTech tracking itself refreshes continuously; a manual RoadTech sync refreshes its TachoMaster evidence." })
        };
    }

    private static ProviderSnapshot RoadTechProvider(DependencyHealthSnapshot snapshot)
    {
        snapshot.Dependencies.TryGetValue("RoadTech", out var tracking);
        snapshot.Dependencies.TryGetValue("TachoMaster", out var tacho);

        var trackingConfigured = tracking is not null && !string.Equals(tracking.Detail, "Dependency is not configured.", StringComparison.OrdinalIgnoreCase);
        var tachoConfigured = tacho is not null && !string.Equals(tacho.Detail, "Dependency is not configured.", StringComparison.OrdinalIgnoreCase);
        var configured = trackingConfigured || tachoConfigured;

        static int Severity(string? status) => status switch
        {
            "Unavailable" => 2,
            "Degraded" => 1,
            "Healthy" => 0,
            _ => 2
        };

        var worst = new[] { tracking, tacho }
            .Where(item => item is not null)
            .OrderByDescending(item => Severity(item!.Status))
            .FirstOrDefault();

        var state = !configured ? "not-configured" : worst?.Status switch
        {
            "Healthy" => "current",
            "Degraded" => "delayed",
            _ => "stale"
        };

        var updated = new[] { tracking?.LastSuccessfulContactUtc, tacho?.LastSuccessfulContactUtc }
            .Where(value => value is not null)
            .Max();
        var detail = $"Tracking: {tracking?.Status ?? "Not configured"}; Tacho: {tacho?.Status ?? "Not configured"}.";
        var cadence = "tracking live every minute · history every 5 minutes · tacho every 20 minutes";

        return new ProviderSnapshot(
            "RoadTech",
            configured,
            state,
            updated,
            worst?.AgeSeconds is null ? null : Math.Round(worst.AgeSeconds.Value / 60d, 1),
            detail,
            cadence);
    }

    private static ProviderSnapshot Provider(string displayName, string dependencyName, DependencyHealthSnapshot snapshot)
    {
        if (!snapshot.Dependencies.TryGetValue(dependencyName, out var dependency))
            return new ProviderSnapshot(displayName, false, "not-configured", null, null, "No canonical dependency state is available.", null);

        var configured = !string.Equals(dependency.Detail, "Dependency is not configured.", StringComparison.OrdinalIgnoreCase);
        var state = dependency.Status switch
        {
            "Healthy" => "current",
            "Degraded" => "delayed",
            _ => configured ? "stale" : "not-configured"
        };
        var cadence = DependencyHealthService.CanonicalCadences.TryGetValue(dependencyName, out var value) ? value : null;
        return new ProviderSnapshot(
            displayName,
            configured,
            state,
            dependency.LastSuccessfulContactUtc,
            dependency.AgeSeconds is null ? null : Math.Round(dependency.AgeSeconds.Value / 60d, 1),
            dependency.Detail,
            cadence);
    }

    private sealed record ProviderSnapshot(
        string Name,
        bool Configured,
        string State,
        DateTimeOffset? LastUpdatedUtc,
        double? AgeMinutes,
        string? Detail,
        string? Cadence);
}
