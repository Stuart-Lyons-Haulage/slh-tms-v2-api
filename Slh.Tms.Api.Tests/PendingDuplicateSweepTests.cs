using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PendingDuplicateSweepTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public PendingDuplicateSweepTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Closes_exact_copies_of_approved_orders_and_leaves_amendments()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        const string order = "{\"poNumber\":\"PO00508240-X\",\"collectionDate\":\"2026-10-19\",\"sellerName\":\"Runcton\",\"stallNumber\":\"Tamworth\",\"pallets\":5}";
        var approved = new StagedImport { EntityType = "order", IdempotencyKey = $"a-{Guid.NewGuid():N}", Status = StagingStatus.Promoted, PayloadJson = order };
        var copy = new StagedImport { EntityType = "order", IdempotencyKey = $"c-{Guid.NewGuid():N}", Status = StagingStatus.PendingReview, PayloadJson = order };
        var amended = new StagedImport { EntityType = "order", IdempotencyKey = $"m-{Guid.NewGuid():N}", Status = StagingStatus.PendingReview, PayloadJson = order.Replace("\"pallets\":5", "\"pallets\":6") };
        db.StagedImports.AddRange(approved, copy, amended);
        await db.SaveChangesAsync();

        Assert.True(await PendingDuplicateSweep.RunAsync(db, CancellationToken.None) >= 1);

        Assert.Equal(StagingStatus.Archived, (await db.StagedImports.AsNoTracking().SingleAsync(x => x.Id == copy.Id)).Status);
        Assert.Equal(StagingStatus.PendingReview, (await db.StagedImports.AsNoTracking().SingleAsync(x => x.Id == amended.Id)).Status);
    }
}
