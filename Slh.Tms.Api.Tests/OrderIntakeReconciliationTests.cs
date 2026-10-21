using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeReconciliationTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public OrderIntakeReconciliationTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Inbound_source_without_order_is_visible_parser_failure_but_outbound_plan_is_excluded()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.StagedImports.AddRange(
            Evidence("inbound-msg", "orders@customer.test", "Inbound", true, "Customer booking"),
            Evidence("outbound-msg", "planner@lyonshaulage.com", "OutboundLyonsPlan", false, "Lyons Collections"));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<OrderIntakeReconciliationService>();
        var result = await service.CompareAsync(DateTimeOffset.UtcNow.AddDays(-1), false, CancellationToken.None);

        Assert.Contains(result.Records, x => x.MessageId == "inbound-msg" && x.Status == "ParserFailure");
        Assert.Contains(result.Records, x => x.MessageId == "outbound-msg" && x.Status == "Excluded");
        Assert.Equal(1, result.OutboundPlansExcluded);
    }

    [Fact]
    public void Weekday_schedule_skips_weekends_and_remains_in_operating_window()
    {
        var fridayEvening = DateTimeOffset.Parse("2026-10-23T20:30:00Z");
        var next = OrderIntakeReconciliationWorker.NextRun(fridayEvening);
        var uk = TimeZoneInfo.ConvertTime(next, ResolveUk());

        Assert.Equal(DayOfWeek.Monday, uk.DayOfWeek);
        Assert.Equal(6, uk.Hour);
    }

    private static StagedImport Evidence(string messageId, string sender, string direction, bool candidate, string subject) => new()
    {
        EntityType = "email-evidence",
        IdempotencyKey = $"evidence:{Guid.NewGuid():N}",
        Status = StagingStatus.Archived,
        Source = "Info mailbox evidence",
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        PayloadJson = JsonSerializer.Serialize(new { messageId, senderAddress = sender, subject, sourceDirection = direction, isOrderCandidate = candidate, attachments = Array.Empty<object>() })
    };

    private static TimeZoneInfo ResolveUk()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }
}
