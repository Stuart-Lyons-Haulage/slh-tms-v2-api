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
            Provider("DOT / Falcon", "RoadTech", snapshot),
            Provider("TachoMaster", "TachoMaster", snapshot),
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
                dotLive = "every minute",
                trackingHistory = "every 5 minutes",
                tachoMaster = "every 5 minutes",
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
            "tacho" or "tachomaster" => Ok(await coordinator.SyncTachoMasterAsync(actor, ct)),
            "sage" or "sagehr" or "sage-hr" => Ok(await coordinator.SyncSageHrAsync(actor, ct)),
            "fleetio" => Ok(await coordinator.SyncFleetioAsync(actor, ct)),
            "all" => Ok(await coordinator.ForceAllAsync(actor, ct)),
            _ => BadRequest(new { message = "Provider must be tacho, sage, fleetio or all." })
        };
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
