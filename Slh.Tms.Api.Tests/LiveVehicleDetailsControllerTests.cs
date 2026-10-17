using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class LiveVehicleDetailsControllerTests
{
    [Fact]
    public void Endpoint_exposes_expected_live_vehicle_route()
    {
        var controllerRoute = typeof(LiveVehicleDetailsController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Single();
        var method = typeof(LiveVehicleDetailsController).GetMethod(nameof(LiveVehicleDetailsController.Get));
        var getRoute = method!
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: true)
            .Cast<HttpGetAttribute>()
            .Single();

        Assert.Equal("api/v1/live/vehicles", controllerRoute.Template);
        Assert.Equal("{vehicleId}/details", getRoute.Template);
    }

    [Fact]
    public void Detail_contract_keeps_tracking_driver_tacho_run_geofence_and_compliance_evidence_separate()
    {
        var vehicleId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var dutyStart = new DateTimeOffset(2026, 8, 21, 5, 42, 0, TimeSpan.Zero);
        var lastSeen = new DateTimeOffset(2026, 8, 21, 12, 51, 0, TimeSpan.Zero);

        var response = new LiveVehicleDetailResponse(
            new LiveVehicleSummary(vehicleId, "EK21XFT", "XFT", "XFT"),
            new LiveTrackingSummary("Moving", lastSeen, lastSeen, 50.85m, -0.75m, 81m, true, true, 1, "Received"),
            new LiveDriverSummary(driverId, "Edward Peter Morgan", "••••1234", "Confirmed", "Edward Peter Morgan", "Edward Peter Morgan", "1234"),
            new LiveTachoSummary(42, dutyStart, null, 180, 45, 192, 900),
            new LiveRunSummary(runId, "45892", "InProgress"),
            new LiveGeofenceSummary("Inside", "Chichester RDC", lastSeen.AddMinutes(-8), 8, lastSeen),
            new LiveComplianceSummary("Matched", "Live tracking, TachoMaster duty and the TMS driver identity are aligned."),
            lastSeen);

        Assert.Equal("EK21XFT", response.Vehicle.Registration);
        Assert.Equal("Moving", response.Tracking.State);
        Assert.Equal("Confirmed", response.Driver.IdentityState);
        Assert.Equal("••••1234", response.Driver.MaskedTachoCard);
        Assert.NotNull(response.Tacho);
        Assert.Equal(dutyStart, response.Tacho!.DutyStartUtc);
        Assert.Equal("45892", response.Run!.Reference);
        Assert.Equal("Inside", response.Geofence!.State);
        Assert.Equal("Matched", response.Compliance.Status);
    }
}
