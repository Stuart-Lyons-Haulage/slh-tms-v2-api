using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class IntegrationSyncCoordinatorVehicleTests
{
    [Theory]
    [InlineData("BL70RKZ")]
    [InlineData("BL70 RKZ")]
    [InlineData("bl70rkz")]
    [InlineData(" bl70 rkz ")]
    public void Vehicle_registration_is_canonicalised_for_identity_matching(string value)
    {
        Assert.Equal("BL70RKZ", IntegrationSyncCoordinator.CanonicalVehicleRegistration(value));
    }

    [Fact]
    public void Registration_match_wins_over_stale_fleetio_mapping()
    {
        var canonical = new Vehicle { Registration = "BL70RKZ" };
        var staleMappedVehicle = new Vehicle { Registration = "XY70ABC" };
        var vehicles = new List<Vehicle> { staleMappedVehicle, canonical };
        var asset = Asset("fleetio-123", "BL70 RKZ");

        var resolved = IntegrationSyncCoordinator.ResolveVehicleForFleetioAsset(
            vehicles,
            asset,
            staleMappedVehicle.Id);

        Assert.Same(canonical, resolved);
    }

    [Fact]
    public void Equivalent_registration_spacing_and_case_reuse_existing_vehicle()
    {
        var existing = new Vehicle { Registration = "BL70 RKZ" };
        var vehicles = new List<Vehicle> { existing };
        var asset = Asset("fleetio-123", "bl70rkz");

        var resolved = IntegrationSyncCoordinator.ResolveVehicleForFleetioAsset(vehicles, asset, null);

        Assert.Same(existing, resolved);
    }

    [Fact]
    public void Fleetio_mapping_can_follow_a_legitimate_registration_change_when_new_registration_is_unused()
    {
        var mapped = new Vehicle { Registration = "AB12CDE", FleetioId = "fleetio-123" };
        var vehicles = new List<Vehicle> { mapped };
        var asset = Asset("fleetio-123", "XY24ZZZ");

        var resolved = IntegrationSyncCoordinator.ResolveVehicleForFleetioAsset(vehicles, asset, mapped.Id);

        Assert.Same(mapped, resolved);
    }

    [Fact]
    public void Duplicate_source_registration_formats_resolve_to_one_tms_vehicle()
    {
        var existing = new Vehicle { Registration = "BL70RKZ" };
        var vehicles = new List<Vehicle> { existing };

        var first = IntegrationSyncCoordinator.ResolveVehicleForFleetioAsset(vehicles, Asset("fleetio-1", "BL70RKZ"), null);
        var second = IntegrationSyncCoordinator.ResolveVehicleForFleetioAsset(vehicles, Asset("fleetio-2", "BL70 RKZ"), null);

        Assert.Same(existing, first);
        Assert.Same(existing, second);
    }

    [Fact]
    public void Last_three_registration_characters_are_not_enough_to_merge_vehicles()
    {
        var existing = new Vehicle { Registration = "AB70RKZ" };
        var vehicles = new List<Vehicle> { existing };
        var asset = Asset("fleetio-2", "CD70RKZ");

        var resolved = IntegrationSyncCoordinator.ResolveVehicleForFleetioAsset(vehicles, asset, null);

        Assert.Null(resolved);
    }

    private static FleetioVehicle Asset(string id, string registration) => new(
        id,
        registration,
        registration,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);
}
