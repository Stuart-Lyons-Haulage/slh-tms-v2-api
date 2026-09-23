using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmbeddedGeofenceSqlProjectionTests
{
    [Fact]
    public async Task Projection_persists_embedded_enter_and_exit_with_stable_identity()
    {
        await using var factory = new CustomWebFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();

        var fence = EmbeddedGeofenceEngine.ApprovedFences.First(x => x.Name.Contains("Selsey", StringComparison.OrdinalIgnoreCase));
        var vehicle = new Vehicle { Id = Guid.NewGuid(), Registration = "TEST123", Active = true };
        db.Vehicles.Add(vehicle);
        db.SiteGeofences.Add(new SiteGeofence
        {
            Id = Guid.NewGuid(),
            Name = fence.Name,
            NormalizedName = new string(fence.Name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()),
            SiteNumber = fence.SiteNumber,
            PolygonJson = "[]",
            Active = true
        });
        await db.SaveChangesAsync();

        var visit = new DerivedVisit
        {
            Id = Guid.NewGuid(),
            VehicleId = vehicle.Id,
            VehicleIdentifier = vehicle.Registration,
            Fence = fence,
            EnteredAtUtc = new DateTimeOffset(2026, 8, 25, 5, 0, 0, TimeSpan.Zero),
            ConfirmedAtUtc = new DateTimeOffset(2026, 8, 25, 5, 10, 0, TimeSpan.Zero),
            ExitedAtUtc = new DateTimeOffset(2026, 8, 25, 5, 25, 0, TimeSpan.Zero),
            LastInsideAtUtc = new DateTimeOffset(2026, 8, 25, 5, 24, 0, TimeSpan.Zero),
            DwellMinutes = 25
        };
        var snapshot = new EmbeddedGeofenceSnapshot([fence], [visit], [], [visit], 3, visit.ExitedAtUtc);

        await EmbeddedGeofenceSqlProjection.PersistAsync(db, snapshot, CancellationToken.None);
        db.ChangeTracker.Clear();
        await EmbeddedGeofenceSqlProjection.PersistAsync(db, snapshot, CancellationToken.None);

        var rows = await db.GeofenceVisits.AsNoTracking().Where(x => x.VehicleId == vehicle.Id).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(visit.Id, row.Id);
        Assert.Equal(visit.EnteredAtUtc, row.EnteredAtUtc);
        Assert.Equal(visit.ExitedAtUtc, row.ExitedAtUtc);
        Assert.Equal("Departed", row.Status);
        Assert.Contains("embedded", row.StatusReason!, StringComparison.OrdinalIgnoreCase);
    }
}
