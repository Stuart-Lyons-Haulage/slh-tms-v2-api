using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class UnreadCardAmendmentTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public UnreadCardAmendmentTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Saving_a_couldnt_read_card_uses_typed_sites_and_becomes_a_real_order_with_a_copy_for_the_second_order()
    {
        var compact = Guid.NewGuid().ToString("N");
        var card = new StagedImport
        {
            EntityType = "order",
            IdempotencyKey = $"email:{compact}:mapping-exception",
            Status = StagingStatus.PendingReview,
            Source = "Info mailbox mapping exception / Grazyna.Zalewska@barfoots.co.uk",
            PayloadJson = JsonSerializer.Serialize(new
            {
                customerCode = "BARFOOTS", collectionSiteName = "Unknown", deliverySiteName = "Unknown",
                sourceMessageId = compact, intakeStatus = "MappingException"
            })
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(card);
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");

        var copy = await client.PostAsync($"/api/v1/staging/{card.Id}/copy-undated", null);
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);

        var save = await client.PutAsJsonAsync($"/api/v1/staging/{card.Id}/payload", new
        {
            payload = new
            {
                poNumber = "0332403646/LEY", customerCode = "BARFOOTS", collectionSite = "Barfoots Leythorne", sellerName = "Barfoots Leythorne",
                deliverySite = "Bedford", stallNumber = "Bedford", pallets = "21", collectionDate = "2026-10-20", deliveryDate = "2026-10-20",
                collectionSiteName = "Unknown", deliverySiteName = "Unknown", sourceMessageId = compact
            },
            note = "test"
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        using var check = factory.Services.CreateScope();
        var rows = await check.ServiceProvider.GetRequiredService<TmsDbContext>().StagedImports.AsNoTracking()
            .Where(item => item.IdempotencyKey.StartsWith($"email:{compact}:")).ToListAsync();
        var saved = Assert.Single(rows, item => item.Id == card.Id);
        Assert.DoesNotContain(":mapping-exception", saved.IdempotencyKey);
        using var payload = JsonDocument.Parse(saved.PayloadJson);
        Assert.Equal("Barfoots Leythorne", payload.RootElement.GetProperty("collectionSiteName").GetString());
        Assert.Equal("Bedford", payload.RootElement.GetProperty("stallNumber").GetString());
        var second = Assert.Single(rows, item => item.Id != card.Id);
        Assert.Equal(StagingStatus.PendingReview, second.Status);
        using var secondPayload = JsonDocument.Parse(second.PayloadJson);
        Assert.False(secondPayload.RootElement.TryGetProperty("collectionDate", out _));
        Assert.Equal(compact, secondPayload.RootElement.GetProperty("sourceMessageId").GetString());
    }
}
