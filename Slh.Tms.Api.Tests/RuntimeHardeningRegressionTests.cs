using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RuntimeHardeningRegressionTests
{
    [Fact]
    public void Jobs_host_never_mutates_database_triggers_at_runtime()
    {
        var source = Read("Slh.Tms.Jobs", "Program.cs");
        Assert.DoesNotContain("DROP TRIGGER", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DISABLE TRIGGER", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TRIGGER", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fleet_write_routes_delegate_to_the_canonical_coordinator()
    {
        var standard = Read("Controllers", "FleetioAssetSyncController.cs");
        var resilient = Read("Controllers", "FleetioResilientSyncController.cs");

        Assert.Contains("coordinator.SyncFleetioAsync", standard, StringComparison.Ordinal);
        Assert.Contains("coordinator.SyncFleetioAsync", resilient, StringComparison.Ordinal);

        var standardWrite = SliceFrom(standard, "[HttpPost(\"sync-assets\")]");
        var resilientWrite = SliceFrom(resilient, "[HttpPost(\"sync-assets-resilient\")]");
        Assert.DoesNotContain("db.Vehicles.Add", standardWrite, StringComparison.Ordinal);
        Assert.DoesNotContain("db.Set<Vehicle>().Add", standardWrite, StringComparison.Ordinal);
        Assert.DoesNotContain("db.Vehicles.Add", resilientWrite, StringComparison.Ordinal);
        Assert.DoesNotContain("db.Set<Vehicle>().Add", resilientWrite, StringComparison.Ordinal);
    }

    [Fact]
    public void Sage_write_route_delegates_to_the_canonical_coordinator()
    {
        var source = Read("Controllers", "IntegrationsController.cs");
        var write = SliceFrom(source, "[HttpPost(\"sage-hr/sync-drivers\")");
        Assert.Contains("coordinator.SyncSageHrAsync", write, StringComparison.Ordinal);
        Assert.DoesNotContain("db.Drivers.Add", write, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO dbo.Drivers", write, StringComparison.Ordinal);
        Assert.DoesNotContain("sagehrsync:{Guid.NewGuid", write, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_tacho_observation_cannot_create_driver_master_rows()
    {
        var source = Read("Services", "TachoObservedDriverSyncService.cs");
        Assert.DoesNotContain("db.Drivers.Add", source, StringComparison.Ordinal);
        Assert.Contains("driverreview:member:", source, StringComparison.Ordinal);
        Assert.Contains("PendingReview", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Successful_audit_outbox_replay_clears_large_payload()
    {
        var source = Read("Services", "AuditOutboxBackgroundService.cs");
        Assert.Contains("item.Payload = string.Empty", source, StringComparison.Ordinal);
        Assert.Contains("ClearProcessedPayloadsAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Fresh_tacho_bootstrap_is_one_time_recent_card_only_and_admin_guarded()
    {
        var source = Read("Controllers", "FreshBootstrapController.cs");
        Assert.Contains("Authorize(Policy = \"TmsAdmin\")", source, StringComparison.Ordinal);
        Assert.Contains("existingCount != 0", source, StringComparison.Ordinal);
        Assert.Contains("TachoDriverCardReadEligibility.IsEligible", source, StringComparison.Ordinal);
        Assert.Contains("GroupBy(x => x.MemberCode)", source, StringComparison.Ordinal);
        Assert.Contains("eligible.Count < 25", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Workbook_cannot_create_driver_or_vehicle_master_rows()
    {
        var source = Read("Controllers", "MasterDataWorkbookImportController.cs");
        var drivers = SliceMethod(source, "private async Task ProcessDriversAsync", "private static DateOnly?");
        var vehicles = SliceMethod(source, "private async Task ProcessVehiclesAsync", "private async Task ProcessCustomerContactsAsync");

        Assert.DoesNotContain("db.Drivers.Add", drivers, StringComparison.Ordinal);
        Assert.DoesNotContain("PromoteDirect(\"driver\"", drivers, StringComparison.Ordinal);
        Assert.DoesNotContain("PromoteDirect(\"vehicle\"", vehicles, StringComparison.Ordinal);
        Assert.DoesNotContain("new Vehicle", vehicles, StringComparison.Ordinal);
        Assert.Contains("update-only", vehicles, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Archive_requires_server_readiness_marker_before_any_cleanup()
    {
        var source = Read("Services", "NightlyArchiveBackgroundService.cs");
        Assert.Contains("SLH_TMS_ARCHIVE_READY.txt", source, StringComparison.Ordinal);
        Assert.Contains("if (!File.Exists(marker))", source, StringComparison.Ordinal);
        Assert.Contains("WriteVerifiedArchiveAsync", source, StringComparison.Ordinal);
        Assert.Contains("HashFileAsync", source, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Expected method bounds not found: {startMarker}");
        return source[start..end];
    }

    private static string SliceFrom(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected marker not found: {marker}");
        var next = source.IndexOf("\n    [Http", start + marker.Length, StringComparison.Ordinal);
        return next > start ? source[start..next] : source[start..];
    }

    private static string Read(params string[] parts)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Slh.Tms.Api.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
