using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class AuditOutboxTests
{
    [Fact]
    public async Task SaveChanges_converts_master_audit_to_durable_outbox_event()
    {
        await using var db = CreateDb();
        var customer = new Customer { Code = "OUTBOX-1", Name = "Outbox Test" };
        var audit = new MasterDataAudit
        {
            EntityType = "Customer",
            EntityId = customer.Id,
            Action = "Create",
            ChangesJson = "{\"name\":\"Outbox Test\"}",
            ChangedBy = "test@example.com"
        };

        db.Customers.Add(customer);
        db.MasterDataAudits.Add(audit);
        await db.SaveChangesAsync();

        Assert.True(await db.Customers.AnyAsync(x => x.Id == customer.Id));
        Assert.False(await db.MasterDataAudits.AnyAsync());

        var outbox = await db.AuditOutboxes.SingleAsync();
        Assert.Equal(AuditOutboxEventTypes.MasterDataAudit, outbox.EventType);
        Assert.Null(outbox.ProcessedAt);
        Assert.Null(outbox.FailedAt);
        Assert.Equal(0, outbox.RetryCount);

        var payload = JsonSerializer.Deserialize<MasterDataAudit>(outbox.Payload);
        Assert.NotNull(payload);
        Assert.Equal(audit.Id, payload!.Id);
        Assert.Equal(audit.EntityId, payload.EntityId);
        Assert.Equal(audit.Action, payload.Action);
    }

    [Fact]
    public async Task Processor_materialises_audit_and_marks_outbox_processed()
    {
        await using var db = CreateDb();
        var audit = new MasterDataAudit
        {
            EntityType = "Vehicle",
            EntityId = Guid.NewGuid(),
            Action = "Update",
            ChangesJson = "{\"active\":false}",
            ChangedBy = "tester"
        };

        db.MasterDataAudits.Add(audit);
        await db.SaveChangesAsync();

        var processor = new AuditOutboxProcessor(db, NullLogger<AuditOutboxProcessor>.Instance);
        var processed = await processor.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        var persistedAudit = await db.MasterDataAudits.SingleAsync();
        Assert.Equal(audit.Id, persistedAudit.Id);

        var outbox = await db.AuditOutboxes.SingleAsync();
        Assert.NotNull(outbox.ProcessedAt);
        Assert.Null(outbox.FailedAt);
        Assert.Equal(0, outbox.RetryCount);
    }

    [Fact]
    public async Task Processor_marks_poison_event_failed_after_five_attempts()
    {
        await using var db = CreateDb();
        db.AuditOutboxes.Add(new AuditOutbox
        {
            EventType = AuditOutboxEventTypes.MasterDataAudit,
            Payload = "{not-valid-json}",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveAuditReplayChangesAsync();

        var processor = new AuditOutboxProcessor(db, NullLogger<AuditOutboxProcessor>.Instance);
        for (var attempt = 0; attempt < AuditOutboxProcessor.MaximumRetries; attempt++)
            await processor.ProcessPendingAsync(CancellationToken.None);

        var failed = await db.AuditOutboxes.SingleAsync();
        Assert.Equal(AuditOutboxProcessor.MaximumRetries, failed.RetryCount);
        Assert.NotNull(failed.FailedAt);
        Assert.Null(failed.ProcessedAt);
    }

    [Fact]
    public void Audit_outbox_model_uses_required_table_and_pending_index()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(AuditOutbox));

        Assert.NotNull(entity);
        Assert.Equal("AuditOutbox", entity!.GetTableName());
        Assert.Equal(nameof(AuditOutbox.OutboxId), entity.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Contains(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "IX_AuditOutbox_Pending");
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"audit-outbox-{Guid.NewGuid()}")
            .Options;
        return new TmsDbContext(options);
    }
}
