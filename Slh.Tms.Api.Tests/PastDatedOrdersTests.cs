using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PastDatedOrdersTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public PastDatedOrdersTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Lists_and_closes_only_orders_whose_dates_have_gone()
    {
        Guid past, future;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var old = new StagedImport { EntityType = "order", IdempotencyKey = $"past-{Guid.NewGuid():N}", Status = StagingStatus.PendingReview, Source = "test", PayloadJson = "{\"customerCode\":\"PASTTEST\",\"poNumber\":\"PO-OLD\",\"collectionDate\":\"2020-01-02\",\"deliveryDate\":\"2020-01-03\"}" };
            var ahead = new StagedImport { EntityType = "order", IdempotencyKey = $"future-{Guid.NewGuid():N}", Status = StagingStatus.PendingReview, Source = "test", PayloadJson = "{\"customerCode\":\"PASTTEST\",\"poNumber\":\"PO-NEW\",\"collectionDate\":\"2099-01-02\"}" };
            db.StagedImports.AddRange(old, ahead);
            await db.SaveChangesAsync();
            past = old.Id; future = ahead.Id;
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/staging/queue/past-dated");
        var ids = list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(past, ids);
        Assert.DoesNotContain(future, ids);

        var close = await client.PostAsJsonAsync("/api/v1/staging/queue/past-dated/close", new { ids = new[] { past, future } });
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        var outcome = await close.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, outcome.GetProperty("closed").GetInt32());
        Assert.Equal(1, outcome.GetProperty("skipped").GetInt32());

        using var check = factory.Services.CreateScope();
        var verify = check.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Equal(StagingStatus.Rejected, (await verify.StagedImports.AsNoTracking().SingleAsync(item => item.Id == past)).Status);
        Assert.Equal(StagingStatus.PendingReview, (await verify.StagedImports.AsNoTracking().SingleAsync(item => item.Id == future)).Status);
    }
}
