using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OutlookIntakeTagsTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public OutlookIntakeTagsTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Each_email_gets_orders_in_needs_attention_or_no_orders()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.StagedImports.AddRange(
            new StagedImport { EntityType = "order", IdempotencyKey = "email:good:row1", Status = StagingStatus.PendingReview, PayloadJson = "{\"sourceMessageId\":\"good\",\"collectionDate\":\"2026-10-20\"}" },
            new StagedImport { EntityType = "order", IdempotencyKey = "email:bad:mapping-exception", Status = StagingStatus.PendingReview, PayloadJson = "{\"sourceMessageId\":\"bad\",\"intakeStatus\":\"MappingException\"}" },
            new StagedImport { EntityType = "order", IdempotencyKey = "email:fixed:mapping-exception", Status = StagingStatus.PendingReview, PayloadJson = "{\"sourceMessageId\":\"fixed\",\"intakeStatus\":\"MappingException\",\"collectionDate\":\"2026-10-20\"}" },
            new StagedImport { EntityType = "order", IdempotencyKey = "email:gone:row1", Status = StagingStatus.Rejected, PayloadJson = "{\"sourceMessageId\":\"gone\",\"collectionDate\":\"2026-10-20\"}" });
        await db.SaveChangesAsync();

        var states = await OutlookIntakeTags.StatesAsync(db, DateTimeOffset.UtcNow.AddDays(-1), null, CancellationToken.None);
        Assert.Equal(OutlookIntakeTags.OrdersIn, states["good"]);
        Assert.Equal(OutlookIntakeTags.NeedsAttention, states["bad"]);
        Assert.Equal(OutlookIntakeTags.OrdersIn, states["fixed"]);
        Assert.Equal(OutlookIntakeTags.NoOrders, states["gone"]);
    }
}
